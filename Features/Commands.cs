using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

public sealed class ChatMsg
{
    public string UserId = "";
    public string Login = "";
    public string Name = "";
    public string Text = "";
    public string MessageId = "";
    public HashSet<string> Badges = new();
    public string ReplyBody = "";
    public string ReplyUser = "";

    public bool IsBroadcaster => Badges.Contains("broadcaster");
    public bool IsMod => IsBroadcaster || Badges.Contains("moderator");
    public bool IsVip => Badges.Contains("vip");
    public bool IsSub => Badges.Contains("subscriber") || Badges.Contains("founder");

    public bool Allowed(string access) => access switch
    {
        "everyone" => true,
        "vip" => IsVip || IsMod,
        "sub" => IsSub || IsMod,
        "mod" => IsMod,
        _ => IsBroadcaster,
    };
}

/// <summary>Свои и стандартные команды чата: подстановки, доступ, кулдауны (разделы 6–7).</summary>
public sealed class Commands
{
    private static readonly Regex NameRe = new(@"^![\w-]{1,30}$");
    private static readonly Regex VarRe = new(@"\{(\w+)\}");
    private static readonly Random Rnd = new();

    public static readonly Dictionary<string, string> Access = new()
    {
        ["everyone"] = "Все", ["vip"] = "VIP и модераторы", ["sub"] = "Подписчики и модераторы",
        ["mod"] = "Модераторы", ["broadcaster"] = "Стример",
    };

    private static readonly (string Key, string Name, string[] Aliases, string Desc, int Cooldown)[] StdDefs =
    {
        ("commands", "!commands", new[] { "!команды" }, "Список доступных команд", 10),
        ("skip", "!skip", Array.Empty<string>(), "Голосование за пропуск текущего трека", 0),
        ("followage", "!followage", new[] { "!followdate" }, "Дата начала отслеживания канала", 15),
        ("uptime", "!uptime", Array.Empty<string>(), "Продолжительность эфира", 10),
        ("top", "!top", Array.Empty<string>(), "Топ активности чата за стрим", 30),
        ("topweek", "!topweek", Array.Empty<string>(), "Топ активности чата за неделю", 30),
        ("topall", "!topall", Array.Empty<string>(), "Топ активности чата за всё время", 30),
        ("track", "!track", new[] { "!трек" }, "Текущая композиция плеера (со ссылкой)", 5),
        ("queue", "!queue", new[] { "!очередь" }, "Очередь заказанных треков и сколько она займёт", 10),
        ("mytrack", "!mytrack", new[] { "!мойтрек" }, "Через сколько заиграет ваш трек", 5),
        ("cancel", "!cancel", new[] { "!отмена" }, "Убрать свой последний заказ из очереди", 5),
        ("history", "!history", new[] { "!история" }, "Последние сыгранные треки", 15),
        ("shazam", "!shazam", Array.Empty<string>(), "Распознавание звука выбранного устройства", 15),
        ("clip", "!clip", new[] { "!клип" }, "Клип последних 30 секунд эфира — ссылка в чат", 30),
        ("translate", "!translate", new[] { "!перевод" }, "Перевод текста", 5),
        ("timer", "!timer", new[] { "!таймер" }, "Разовый таймер-напоминание", 3),
        ("timers", "!timers", Array.Empty<string>(), "Ваши разовые таймеры", 5),
        ("canceltimer", "!canceltimer", Array.Empty<string>(), "Отмена таймера по ID", 2),
    };

    public static JsonObject StandardDefaults()
    {
        var o = new JsonObject();
        foreach (var d in StdDefs)
        {
            var aliases = new JsonArray();
            foreach (var a in d.Aliases) aliases.Add(a);
            o[d.Key] = new JsonObject
            {
                ["name"] = d.Name, ["aliases"] = aliases, ["desc"] = d.Desc, ["cooldown"] = d.Cooldown,
                ["access"] = "everyone", ["template"] = "{result}", ["random_max"] = 100,
                ["enabled"] = true, ["deleted"] = false,
            };
        }
        return o;
    }

    private readonly Hub _hub;
    public readonly JsonStore Store;
    private readonly Dictionary<string, double> _cooldowns = new();
    public readonly Dictionary<string, double> UserLast = new();
    private readonly Dictionary<string, Func<ChatMsg, string, Task<string>>> _handlers;

    public Commands(Hub hub)
    {
        _hub = hub;
        Store = new JsonStore(System.IO.Path.Combine(Config.Profile, "commands.json"),
            new JsonObject { ["custom"] = new JsonArray(), ["standard"] = StandardDefaults() });
        FixStandard();
        _handlers = new()
        {
            ["commands"] = HCommands, ["skip"] = HSkip, ["followage"] = HFollowage, ["uptime"] = HUptime,
            ["top"] = HTop("stream"), ["topweek"] = HTop("week"), ["topall"] = HTop("all"), ["track"] = HTrack,
            ["timer"] = HTimer, ["timers"] = HTimers, ["canceltimer"] = HCancelTimer,
            ["shazam"] = HShazam, ["translate"] = HTranslate,
            ["queue"] = (m, a) => Task.FromResult(_hub.Music.QueueText()),
            ["mytrack"] = (m, a) => Task.FromResult(_hub.Music.MyTrack(m.Login, m.Name)),
            ["cancel"] = (m, a) => Task.FromResult(_hub.Music.CancelOwn(m.Login, m.Name)),
            ["clip"] = HClip,
            ["history"] = (m, a) =>
            {
                var list = _hub.Stats.RecentTitles(5);
                return Task.FromResult(list.Count == 0 ? "Пока ничего не играло"
                    : "Недавно играли: " + string.Join(" · ", list.Select((t, i) => $"{i + 1}. {(t.Length > 45 ? t[..44] + "…" : t)}")));
            },
        };
    }

    public JsonArray Custom => (JsonArray)Store.Data["custom"];
    public JsonObject Standard => (JsonObject)Store.Data["standard"];

    private void FixStandard()
    {
        var defs = StandardDefaults();
        var std = Standard;
        foreach (var kv in defs)
            if (!std.ContainsKey(kv.Key)) std[kv.Key] = kv.Value.DeepClone();
        foreach (var k in std.Select(p => p.Key).ToList())
            if (!defs.ContainsKey(k)) std.Remove(k);
    }

    // ---------- поиск и проверка ----------
    public sealed record Found(string Kind, string Key, JsonObject Cmd);

    private static IEnumerable<string> NamesOf(JsonObject c)
    {
        yield return c.Str("name");
        foreach (var a in c.StrList("aliases")) yield return a;
    }

    public Dictionary<string, Found> AllNames(string exKind = null, string exKey = null)
    {
        var names = new Dictionary<string, Found>();
        foreach (var node in Custom)
        {
            var c = (JsonObject)node;
            if (exKind == "custom" && c.Str("id") == exKey) continue;
            foreach (var n in NamesOf(c)) names[n.ToLowerInvariant()] = new Found("custom", c.Str("id"), c);
        }
        foreach (var kv in Standard)
        {
            var c = (JsonObject)kv.Value;
            if (c.Bool("deleted") || (exKind == "standard" && exKey == kv.Key)) continue;
            foreach (var n in NamesOf(c)) names[n.ToLowerInvariant()] = new Found("standard", kv.Key, c);
        }
        return names;
    }

    public Found Lookup(string name) => AllNames().TryGetValue(name.ToLowerInvariant(), out var f) ? f : null;

    private static List<string> AliasesOf(JsonNode cmd)
    {
        var n = cmd["aliases"];
        if (n is JsonArray a) return a.Select(x => J.AsStr(x).Trim()).Where(x => x != "").ToList();
        return J.AsStr(n).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private JsonObject Validate(JsonNode cmd, string exKind, string exKey)
    {
        var name = cmd.Str("name").Trim();
        var aliases = AliasesOf(cmd);
        foreach (var n in new[] { name }.Concat(aliases))
            if (!NameRe.IsMatch(n))
                throw new UserError($"«{n}»: имя начинается с ! и содержит до 30 букв, цифр, _ или - без пробелов");
        var lowered = new[] { name }.Concat(aliases).Select(x => x.ToLowerInvariant()).ToList();
        if (lowered.Distinct().Count() != lowered.Count) throw new UserError("Имя и псевдонимы повторяются");
        var taken = AllNames(exKind, exKey);
        foreach (var n in lowered)
            if (taken.ContainsKey(n)) throw new UserError($"«{n}» уже используется другой командой");
        var rm = cmd.Int("random_max", 100);
        if (rm == 0) rm = 100;
        if (rm < 1 || rm > 1000) throw new UserError("Максимальное случайное число: от 1 до 1000");
        var cd = cmd.Int("cooldown", 0);
        if (cd < 0 || cd > 86400) throw new UserError("Кулдаун: от 0 до 86400 секунд");
        var access = cmd.Str("access", "everyone");
        if (!Access.ContainsKey(access)) throw new UserError("Неизвестный уровень доступа");
        var al = new JsonArray();
        foreach (var a in aliases) al.Add(a);
        return new JsonObject
        {
            ["name"] = name, ["aliases"] = al, ["random_max"] = rm, ["cooldown"] = cd, ["access"] = access,
            ["enabled"] = cmd.Bool("enabled", true),
        };
    }

    public JsonObject SaveCustom(JsonNode cmd)
    {
        var id = cmd.Str("id");
        if (id == "") id = Guid.NewGuid().ToString("N")[..10];
        var clean = Validate(cmd, "custom", id);
        var resp = cmd.Str("response").Trim();
        if (resp == "") throw new UserError("Заполните ответ команды");
        clean["id"] = id;
        clean["response"] = resp.Length > 500 ? resp[..500] : resp;
        var arr = Custom;
        var idx = arr.Select((n, i) => (n, i)).Where(p => p.n.Str("id") == id).Select(p => p.i).DefaultIfEmpty(-1).First();
        if (idx >= 0) { if (arr[idx].Int("count") > 0) clean["count"] = arr[idx].Int("count"); arr[idx] = clean; }
        else arr.Add(clean);
        Store.Save();
        return clean;
    }

    public void DeleteCustom(string id)
    {
        var arr = Custom;
        for (var i = arr.Count - 1; i >= 0; i--)
            if (arr[i].Str("id") == id) arr.RemoveAt(i);
        Store.Save();
    }

    public JsonObject SaveStandard(string key, JsonNode cmd)
    {
        if (!Standard.ContainsKey(key)) throw new UserError("Неизвестная стандартная команда");
        var clean = Validate(cmd, "standard", key);
        var cur = (JsonObject)Standard[key];
        foreach (var kv in clean.ToList()) cur[kv.Key] = kv.Value.DeepClone();
        var tpl = cmd.Str("template", cur.Str("template", "{result}"));
        if (tpl.Length > 500) tpl = tpl[..500];
        cur["template"] = tpl == "" ? "{result}" : tpl;
        Store.Save();
        return cur;
    }

    public void DeleteStandard(string key)
    {
        if (Standard[key] is JsonObject c) { c["deleted"] = true; Store.Save(); }
    }

    public void ResetStandard()
    {
        Store.Data["standard"] = StandardDefaults();
        Store.Save();
    }

    public void SetEnabled(string kind, string key, bool enabled)
    {
        if (kind == "standard" && Standard[key] is JsonObject s) s["enabled"] = enabled;
        else
            foreach (var c in Custom)
                if (c.Str("id") == key) c["enabled"] = enabled;
        Store.Save();
    }

    // ---------- подстановки ----------
    public static string Render(string template, IDictionary<string, string> ctx, int randomMax = 100)
    {
        var max = Math.Max(1, Math.Min(1000, randomMax <= 0 ? 100 : randomMax));
        var n = Rnd.Next(1, max + 1).ToString(CultureInfo.InvariantCulture);
        var values = new Dictionary<string, string>(ctx);
        if (!values.ContainsKey("юзер")) values["юзер"] = values.TryGetValue("user", out var u) ? u : "";
        values["random"] = values["rand"] = values["рандом"] = n;
        return VarRe.Replace(template ?? "", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : m.Value);
    }

    public static string HumanDuration(double seconds)
    {
        var sec = (long)Math.Max(0, seconds);
        long d = sec / 86400, h = sec % 86400 / 3600, m = sec % 3600 / 60, s = sec % 60;
        var parts = new List<string>();
        if (d > 0) parts.Add($"{d} дн.");
        if (h > 0) parts.Add($"{h} ч");
        if (m > 0) parts.Add($"{m} мин");
        if (s > 0 && d == 0 && h == 0) parts.Add($"{s} сек");
        return parts.Count > 0 ? string.Join(" ", parts) : "0 сек";
    }

    private Dictionary<string, string> BaseCtx(ChatMsg msg, string args)
    {
        var ctx = new Dictionary<string, string>
        {
            ["user"] = msg.Name, ["login"] = msg.Login, ["args"] = args,
            ["channel"] = _hub.Twitch.Streamer.Login, ["uptime"] = _hub.Stream.UptimeText(),
            ["game"] = _hub.Stream.Category, ["title"] = _hub.Stream.Title,
        };
        AddArgs(ctx, args, msg.Name);
        return ctx;
    }

    /// <summary>{touser} — ник из первого слова (без @) или сам зритель; {1}…{9} — отдельные слова.</summary>
    private static void AddArgs(Dictionary<string, string> ctx, string args, string user)
    {
        var words = (args ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        ctx["touser"] = words.Length > 0 ? words[0].TrimStart('@') : user;
        for (var i = 1; i <= 9; i++) ctx[i.ToString(CultureInfo.InvariantCulture)] = words.Length >= i ? words[i - 1] : "";
    }

    // ---------- выполнение ----------
    public async Task Handle(ChatMsg msg)
    {
        var text = msg.Text.Trim();
        if (!text.StartsWith("!")) return;
        var sp = text.IndexOfAny(new[] { ' ', '\t' });
        var name = (sp < 0 ? text : text[..sp]).ToLowerInvariant();
        var args = sp < 0 ? "" : text[(sp + 1)..].Trim();
        var found = Lookup(name);
        if (found == null) return;
        var cmd = found.Cmd;
        var log = _hub.Log;
        if (!cmd.Bool("enabled", true))
        {
            log.Add("skip", $"{name} от {msg.Login}: команда выключена — включите её в «Чат → Команды → Стандартные команды»");
            return;
        }
        var access = cmd.Str("access", "everyone");
        if (!msg.Allowed(access))
        {
            log.Add("skip", $"{name} от {msg.Login}: недостаточно прав ({(Access.TryGetValue(access, out var a) ? a : access)})");
            return;
        }
        var now = ActionLog.Now;
        if (!msg.IsBroadcaster)
        {
            var limit = _hub.S.Num("chat.user_rate_limit_sec", 2);
            if (UserLast.TryGetValue(msg.Login, out var last) && now - last < limit)
            {
                log.Add("skip", $"{name} от {msg.Login}: слишком частые запросы");
                return;
            }
        }
        // Свои команды: кулдаун на все вызовы команды. Стандартные: кулдаун на зрителя.
        var cdKey = found.Kind == "custom" ? $"custom|{found.Key}" : $"standard|{found.Key}|{msg.Login}";
        var cd = cmd.Int("cooldown");
        var left = cd - (now - (_cooldowns.TryGetValue(cdKey, out var t) ? t : 0));
        if (cd > 0 && left > 0 && !msg.IsBroadcaster)
        {
            log.Add("skip", $"{name} от {msg.Login}: кулдаун ещё {(int)left + 1} сек");
            return;
        }
        UserLast[msg.Login] = now;
        _cooldowns[cdKey] = now;
        var ctx = BaseCtx(msg, args);
        string reply;
        if (found.Kind == "custom")
        {
            // {count} — сколько раз вызвали команду (счётчик хранится в самой команде)
            if (cmd.Str("response").Contains("{count}"))
            {
                var n = cmd.Int("count") + 1;
                cmd["count"] = n;
                Store.Save();
                ctx["count"] = n.ToString(CultureInfo.InvariantCulture);
            }
            reply = Render(cmd.Str("response"), ctx, cmd.Int("random_max", 100));
        }
        else
        {
            string result;
            try { result = await _handlers[found.Key](msg, args); }
            catch (Exception e) { log.Error($"{name}: ошибка выполнения ({e.Message})"); return; }
            if (result == null) return;
            ctx["result"] = result;
            var tpl = cmd.Str("template", "{result}");
            reply = Render(tpl == "" ? "{result}" : tpl, ctx, cmd.Int("random_max", 100));
        }
        log.Add("command", $"{name} от {msg.Login}");
        await _hub.Twitch.SendChat(reply);
    }

    public string Preview(string template, string user, string args, int randomMax)
    {
        var ctx = new Dictionary<string, string>
        {
            ["user"] = user == "" ? "Зритель" : user, ["login"] = (user == "" ? "viewer" : user).ToLowerInvariant(),
            ["args"] = args, ["channel"] = _hub.Twitch.Streamer.Login == "" ? "channel" : _hub.Twitch.Streamer.Login,
            ["uptime"] = _hub.Stream.UptimeText(), ["result"] = "[результат функции]",
            ["game"] = _hub.Stream.Category == "" ? "Just Chatting" : _hub.Stream.Category,
            ["title"] = _hub.Stream.Title == "" ? "Название стрима" : _hub.Stream.Title, ["count"] = "42",
        };
        AddArgs(ctx, args, ctx["user"]);
        return Render(template, ctx, randomMax);
    }

    // ---------- стандартные функции ----------
    private Task<string> HCommands(ChatMsg msg, string args)
    {
        var names = new List<string>();
        foreach (var c in Custom)
            if (c.Bool("enabled") && msg.Allowed(c.Str("access", "everyone"))) names.Add(c.Str("name"));
        foreach (var kv in Standard)
            if (kv.Value.Bool("enabled") && !kv.Value.Bool("deleted") && msg.Allowed(kv.Value.Str("access", "everyone")))
                names.Add(kv.Value.Str("name"));
        return Task.FromResult(names.Count > 0 ? "Команды: " + string.Join(", ", names) : "Нет доступных команд");
    }

    private Task<string> HSkip(ChatMsg msg, string args) => Task.FromResult(_hub.Music.VoteSkip(msg));

    private async Task<string> HFollowage(ChatMsg msg, string args)
    {
        if (msg.IsBroadcaster) return $"{msg.Name}, это ваш канал 🙂";
        string followed;
        try { followed = await _hub.Twitch.GetFollowedAt(msg.UserId); }
        catch (TwitchException e) when (e.Status is 401 or 403)
        {
            _hub.Log.Error("!followage: нет права чтения фолловеров — войдите в Twitch как стример заново");
            return "Не удалось проверить отслеживание: стримеру нужно заново войти в Twitch";
        }
        if (string.IsNullOrEmpty(followed)) return $"{msg.Name}, вы не отслеживаете канал";
        var dt = DateTimeOffset.Parse(followed, CultureInfo.InvariantCulture);
        var delta = DateTimeOffset.UtcNow - dt;
        return $"{msg.Name} отслеживает канал с {dt.ToLocalTime():dd.MM.yyyy} ({delta.Days} дн. {delta.Hours} ч)";
    }

    private Task<string> HUptime(ChatMsg msg, string args) =>
        Task.FromResult(_hub.Stream.Live ? $"Стрим идёт {_hub.Stream.UptimeText()}" : "Сейчас стрим не идёт");

    private Func<ChatMsg, string, Task<string>> HTop(string period)
    {
        var label = period == "stream" ? "за стрим" : period == "week" ? "за неделю" : "за всё время";
        return (msg, args) =>
        {
            var top = _hub.Stats.TopChat(period, _hub.Stream.Key, 5);
            if (top.Count == 0) return Task.FromResult($"Топ чата {label}: пока пусто");
            var parts = top.Select((r, i) => $"{i + 1}. {r[0]} ({r[1]})");
            return Task.FromResult($"Топ чата {label}: " + string.Join(", ", parts));
        };
    }

    private Task<string> HTrack(ChatMsg msg, string args) => Task.FromResult(_hub.Music.TrackText());

    private Task<string> HTimer(ChatMsg msg, string args) => Task.FromResult(_hub.Timers.CreateViewer(msg, args));
    private Task<string> HTimers(ChatMsg msg, string args) => Task.FromResult(_hub.Timers.ListViewer(msg));
    private Task<string> HCancelTimer(ChatMsg msg, string args) => Task.FromResult(_hub.Timers.CancelViewer(msg, args));

    private async Task<string> HClip(ChatMsg msg, string args)
    {
        if (!_hub.Stream.Live) return $"@{msg.Name}, клип можно сделать только во время эфира";
        if (!_hub.Twitch.Streamer.Scopes.Contains("clips:edit"))
            return $"@{msg.Name}, клипы пока не включены: стримеру нужно перезайти в Twitch в программе Beg1nnnerOS";
        string id;
        try { id = await _hub.Twitch.CreateClip(); }
        catch (TwitchException e)
        {
            _hub.Log.Add("skip", "Клип: " + e.Message);
            return $"@{msg.Name}, " + (e.Status == 404 ? "Twitch не дал создать клип (канал не в эфире?)"
                : e.Status == 403 ? "на канале запрещено создавать клипы" : "не удалось создать клип, попробуйте позже");
        }
        if (id == "") return $"@{msg.Name}, не удалось создать клип";
        // Twitch обрабатывает клип 10–20 секунд
        for (var i = 0; i < 7; i++)
        {
            await Task.Delay(5000);
            JsonNode c;
            try { c = await _hub.Twitch.GetClip(id); } catch { continue; }
            if (c == null) continue;
            var url = c.Str("url");
            _hub.Log.Add("system", $"Клип от {msg.Name}: {url}");
            _hub.Stats.Event(_hub.Stream.Key, "clip", msg.Name, 0, "");
            _hub.Loop.Start(() => _hub.Announce.OnClip(url, c.Str("title"), msg.Name));
            return $"🎬 @{msg.Name}, клип готов: {url}";
        }
        return $"@{msg.Name}, клип ещё обрабатывается — он появится в разделе «Клипы» канала: https://clips.twitch.tv/{id}";
    }

    private async Task<string> HShazam(ChatMsg msg, string args)
    {
        var sh = _hub.Shazam;
        if (!sh.Enabled)
        {
            _hub.Log.Add("skip", "!shazam: модуль Shazam выключен в «Чат → Инструменты»");
            return "Распознавание музыки сейчас выключено";
        }
        try { return "Сейчас звучит: " + await sh.Recognize(); }
        catch (Exception e)
        {
            _hub.Log.Error($"!shazam: {e.Message}");
            return "Не удалось распознать песню, попробуйте позже";
        }
    }

    private async Task<string> HTranslate(ChatMsg msg, string args)
    {
        var tr = _hub.Translator;
        if (!tr.Enabled)
        {
            _hub.Log.Add("skip", "!translate: перевод выключен в «Чат → Инструменты»");
            return null;
        }
        string text = args, who = "";
        if (text == "" && msg.ReplyBody != "") { text = msg.ReplyBody; who = msg.ReplyUser; }
        if (text == "") return $"{msg.Name}, напишите: !translate текст или ответьте командой на сообщение";
        try
        {
            var (outText, _) = await tr.Translate(text);
            return who != "" ? $"{who}: {outText}" : outText;
        }
        catch (Exception e)
        {
            _hub.Log.Error($"!translate: {e.Message}");
            return $"Перевод недоступен: {e.Message}";
        }
    }
}
