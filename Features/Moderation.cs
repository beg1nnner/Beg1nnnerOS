using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Фильтр спама и ссылок: ссылки, капс, повторы, флуд символами и смайлами, запрещённые слова, длинные сообщения.
/// Действия: предупредить, удалить сообщение, таймаут; повторное нарушение — строже. Настройки — раздел «mod».
/// </summary>
public sealed class Moderation
{
    public const string ScopeDelete = "moderator:manage:chat_messages";
    public const string ScopeBan = "moderator:manage:banned_users";

    public sealed class Verdict
    {
        public string Rule = "", Reason = "", Action = "warn", Detail = "";
        public int Timeout;
        public string UserId = "", Login = "", Name = "", MessageId = "", Text = "";
        public bool Hides => Action is "delete" or "timeout";
    }

    public static readonly (string Key, string Title, string Reason)[] Rules =
    {
        ("links", "Ссылки", "ссылки в чате запрещены"),
        ("words", "Запрещённые слова", "такие слова здесь запрещены"),
        ("repeat", "Повторы", "не повторяй одно и то же"),
        ("caps", "Капс", "не пиши капсом"),
        ("symbols", "Флуд символами и смайлами", "без флуда символами и смайлами"),
        ("length", "Длинные сообщения", "слишком длинное сообщение"),
    };

    private readonly Hub _hub;
    private readonly Dictionary<string, List<(double Ts, string Text)>> _recent = new();
    private readonly Dictionary<string, (double Ts, int Count)> _strikes = new();
    private readonly Dictionary<string, double> _permits = new();
    private readonly List<JsonObject> _journal = new();
    private double _lastNotify, _lastError;

    public Moderation(Hub hub) => _hub = hub;
    private JsonObject Cfg => _hub.S.Obj("mod") ?? new JsonObject();
    private TwitchClient Tw => _hub.Twitch;

    private static readonly Regex UrlRe = new(
        @"(?:https?://|www\.)\S+|\b(?:[a-zа-яё0-9](?:[a-zа-яё0-9-]{0,61}[a-zа-яё0-9])?\.)+(?:com|net|org|ru|su|ua|by|kz|io|gg|tv|me|xyz|info|top|site|online|shop|store|live|pro|app|dev|link|ly|cc|biz|club|fun|tk|ml|ga|cf|gq|ink|bio|win|vip|click|be|gl|to|ws|rf|рф|онлайн|сайт)\b(?:/\S*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DotTrickRe = new(@"\s*[\(\[\{]\s*(?:dot|точка|тчк)\s*[\)\]\}]\s*|\s+(?:dot|точка)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpaceRe = new(@"\s+", RegexOptions.Compiled);

    // ---------- проверка ----------
    /// <summary>Проверяет сообщение. null — всё в порядке. Модераторы и стример не проверяются.</summary>
    public Verdict Check(JsonNode ev, string uid, string login, string name, string text, HashSet<string> badges)
    {
        var c = Cfg;
        if (!c.Bool("enabled")) return null;
        if (badges.Contains("broadcaster") || badges.Contains("moderator")) return null;
        if (c.Bool("exempt_vip", true) && badges.Contains("vip")) return null;
        if (c.Bool("exempt_sub") && (badges.Contains("subscriber") || badges.Contains("founder"))) return null;
        var (plain, emotes) = Split(ev, text);
        var v = Evaluate(c, uid, login, text, plain, emotes, true);
        if (v == null) return null;
        v.UserId = uid; v.Login = login; v.Name = name; v.MessageId = ev?.Str("message_id") ?? ""; v.Text = text;
        Escalate(c, v);
        return v;
    }

    /// <summary>Проверка фразы из панели — без действий и без учёта повторов.</summary>
    public object Test(string text)
    {
        var c = Cfg;
        var v = Evaluate(c, "", "", text ?? "", text ?? "", 0, false);
        if (v == null) return new { ok = true, message = c.Bool("enabled") ? "Сообщение прошло бы фильтр" : "Сообщение прошло бы фильтр (фильтр сейчас выключен)" };
        return new { ok = false, message = $"Сработало правило «{RuleTitle(v.Rule)}»{(v.Detail != "" ? ": " + v.Detail : "")} → {ActionTitle(v.Action, v.Timeout)}" };
    }

    private Verdict Evaluate(JsonObject c, string uid, string login, string text, string plain, int emotes, bool track)
    {
        var now = ActionLog.Now;
        Verdict Hit(string rule, string detail = "")
        {
            var r = c.Obj(rule) ?? new JsonObject();
            var action = r.Str("action", "delete");
            if (action is not ("warn" or "delete" or "timeout")) action = "delete";
            return new Verdict
            {
                Rule = rule, Detail = detail, Action = action, Timeout = Math.Clamp(r.Int("timeout", 60), 1, 1209600),
                Reason = Rules.First(x => x.Key == rule).Reason,
            };
        }
        bool On(string rule) => c.Bool(rule + ".enabled");

        // ссылки
        if (On("links"))
        {
            var bad = FindLinks(text).FirstOrDefault(h => !Allowed(c, h));
            if (bad != null)
            {
                if (track && _permits.TryGetValue(login, out var until) && until > now) { _permits.Remove(login); }
                else return Hit("links", bad);
            }
        }
        // запрещённые слова
        if (On("words"))
        {
            var norm = " " + Normalize(text) + " ";
            foreach (var w in c.StrList("words.list"))
            {
                var whole = w.StartsWith("=");
                var nw = Normalize(whole ? w[1..] : w);
                if (nw == "") continue;
                if (whole ? norm.Contains(" " + nw + " ") : norm.Contains(nw)) return Hit("words", w);
            }
        }
        // повторы (одно и то же сообщение несколько раз подряд)
        if (On("repeat") && track && uid != "")
        {
            var key = SpaceRe.Replace(text.Trim().ToLowerInvariant(), " ");
            var window = Math.Clamp(c.Int("repeat.window", 30), 5, 600);
            if (!_recent.TryGetValue(uid, out var list)) _recent[uid] = list = new();
            list.RemoveAll(x => now - x.Ts > window);
            list.Add((now, key));
            var same = list.Count(x => x.Text == key);
            if (same >= Math.Max(2, c.Int("repeat.count", 3))) { list.RemoveAll(x => x.Text == key); return Hit("repeat", $"{same} раза"); }
            if (_recent.Count > 3000) foreach (var k in _recent.Where(p => p.Value.Count == 0 || now - p.Value[^1].Ts > 600).Select(p => p.Key).ToList()) _recent.Remove(k);
        }
        // капс (без учёта смайлов)
        if (On("caps"))
        {
            var letters = plain.Where(char.IsLetter).ToList();
            if (letters.Count >= Math.Max(3, c.Int("caps.min_len", 15)))
            {
                var pct = letters.Count(char.IsUpper) * 100 / letters.Count;
                if (pct >= Math.Clamp(c.Int("caps.percent", 70), 30, 100)) return Hit("caps", $"{pct}% заглавных");
            }
        }
        // флуд символами и смайлами
        if (On("symbols"))
        {
            var run = Math.Max(4, c.Int("symbols.run", 12));
            var m = Regex.Match(plain, "(.)\\1{" + (run - 1) + ",}");
            if (m.Success && !char.IsWhiteSpace(m.Value[0])) return Hit("symbols", $"«{m.Value[0]}» × {m.Length}");
            var maxEm = c.Int("symbols.emotes", 10);
            if (maxEm > 0 && emotes > maxEm) return Hit("symbols", $"{emotes} смайлов");
        }
        // длина
        if (On("length"))
        {
            var max = Math.Max(20, c.Int("length.max", 350));
            if (text.Length > max) return Hit("length", $"{text.Length} символов");
        }
        return null;
    }

    /// <summary>Повторное нарушение за N минут — строже: предупреждение → удаление → таймаут, дальше таймаут длиннее.</summary>
    private void Escalate(JsonObject c, Verdict v)
    {
        if (!c.Bool("escalate", true)) return;
        var now = ActionLog.Now;
        var win = Math.Clamp(c.Int("escalate_min", 10), 1, 1440) * 60;
        var n = _strikes.TryGetValue(v.UserId, out var s) && now - s.Ts < win ? s.Count + 1 : 1;
        _strikes[v.UserId] = (now, n);
        if (n < 2) return;
        if (v.Action == "warn") v.Action = n == 2 ? "delete" : "timeout";
        else if (v.Action == "delete") v.Action = "timeout";
        else v.Timeout = Math.Min(v.Timeout * (n - 1) * 2, 86400);
        if (v.Action == "timeout") v.Timeout = Math.Max(v.Timeout, c.Int(v.Rule + ".timeout", 60));
        v.Detail = (v.Detail == "" ? "" : v.Detail + ", ") + $"нарушение №{n}";
    }

    // ---------- действие ----------
    public async Task Apply(Verdict v)
    {
        var c = Cfg;
        var ok = true;
        var err = "";
        try
        {
            if (v.Action == "delete" && v.MessageId != "")
                await Tw.Api(HttpMethod.Delete, "/moderation/chat", new()
                    { ["broadcaster_id"] = Tw.BroadcasterId, ["moderator_id"] = Tw.Streamer.UserId, ["message_id"] = v.MessageId });
            else if (v.Action == "timeout")
                await Tw.Api(HttpMethod.Post, "/moderation/bans",
                    new() { ["broadcaster_id"] = Tw.BroadcasterId, ["moderator_id"] = Tw.Streamer.UserId },
                    new { data = new { user_id = v.UserId, duration = v.Timeout, reason = $"Beg1nnnerOS: {v.Reason}" } });
        }
        catch (TwitchException e)
        {
            ok = false;
            err = e.Status is 401 or 403
                ? "нет прав модератора — нажмите «Войти в Twitch заново» в программе"
                : e.Reason;
            if (ActionLog.Now - _lastError > 60) { _lastError = ActionLog.Now; _hub.Log.Add("error", $"Модерация: {err}"); }
        }
        var actTitle = ActionTitle(v.Action, v.Timeout);
        _hub.Log.Add("mod", $"{v.Name}: {RuleTitle(v.Rule)}{(v.Detail != "" ? " (" + v.Detail + ")" : "")} → {actTitle}{(ok ? "" : " — не получилось: " + err)} · «{Cut(v.Text, 80)}»");
        lock (_journal)
        {
            _journal.Insert(0, new JsonObject
            {
                ["ts"] = ActionLog.Now, ["user"] = v.Name, ["login"] = v.Login, ["user_id"] = v.UserId, ["text"] = Cut(v.Text, 300),
                ["rule"] = RuleTitle(v.Rule), ["detail"] = v.Detail, ["action"] = v.Action, ["action_title"] = actTitle,
                ["timeout"] = v.Timeout, ["ok"] = ok, ["error"] = err,
            });
            if (_journal.Count > 100) _journal.RemoveRange(100, _journal.Count - 100);
        }
        // сообщение в чат: не чаще раза в 5 секунд, чтобы бот сам не устроил флуд во время рейда
        if (c.Bool("notify", true) && ActionLog.Now - _lastNotify >= 5)
        {
            _lastNotify = ActionLog.Now;
            var tpl = v.Action == "timeout" ? c.Str("timeout_text", "{user} отдыхает {time}: {reason}") : c.Str("warn_text", "@{user}, {reason}");
            var text = tpl.Replace("{user}", v.Name).Replace("{reason}", v.Reason).Replace("{time}", Dur(v.Timeout)).Trim();
            if (text != "") { try { await Tw.SendChat(text); } catch { } }
        }
    }

    /// <summary>!разрешить ник / !permit ник — модератор разрешает одну ссылку на время.</summary>
    public async Task<bool> OnCommand(ChatMsg m)
    {
        if (!Cfg.Bool("enabled") || !m.IsMod) return false;
        var parts = m.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].ToLowerInvariant() is not ("!разрешить" or "!permit")) return false;
        if (parts.Length < 2) { await Tw.SendChat($"@{m.Name}, напиши: {parts[0]} ник"); return true; }
        var who = parts[1].TrimStart('@').ToLowerInvariant();
        var sec = Math.Clamp(Cfg.Int("links.permit_sec", 60), 10, 600);
        _permits[who] = ActionLog.Now + sec;
        await Tw.SendChat($"@{parts[1].TrimStart('@')}, можно отправить одну ссылку в течение {Dur(sec)}");
        _hub.Log.Add("mod", $"{m.Name} разрешил(а) ссылку для {who} на {Dur(sec)}");
        return true;
    }

    public async Task<string> Unban(string userId)
    {
        if (string.IsNullOrEmpty(userId)) throw new UserError("Не указан зритель");
        try
        {
            await Tw.Api(HttpMethod.Delete, "/moderation/bans", new()
                { ["broadcaster_id"] = Tw.BroadcasterId, ["moderator_id"] = Tw.Streamer.UserId, ["user_id"] = userId });
        }
        catch (TwitchException e)
        {
            if (e.Status == 400) return "Таймаут уже закончился";
            throw new UserError(e.Status is 401 or 403 ? "Нет прав модератора — войдите в Twitch заново" : "Twitch: " + e.Reason);
        }
        _strikes.Remove(userId);
        return "Таймаут снят";
    }

    public object State()
    {
        var sc = Tw.Streamer.Scopes;
        List<JsonObject> items;
        lock (_journal) items = _journal.Select(j => (JsonObject)j.DeepClone()).ToList();
        var today = DateTime.Now.Date;
        return new
        {
            ready = Tw.Streamer.Ready,
            scopes = sc.Contains(ScopeDelete) && sc.Contains(ScopeBan),
            items,
            count = items.Count(i => DateTimeOffset.FromUnixTimeMilliseconds((long)(i.Num("ts") * 1000)).LocalDateTime.Date == today),
        };
    }

    // ---------- вспомогательное ----------
    /// <summary>Текст без смайлов Twitch и число смайлов.</summary>
    private static (string Plain, int Emotes) Split(JsonNode ev, string text)
    {
        if (ev?.At("message.fragments") is not JsonArray frags || frags.Count == 0) return (text, 0);
        var sb = new StringBuilder();
        var emotes = 0;
        foreach (var f in frags)
        {
            if (f.Str("type") == "emote") { emotes++; sb.Append(' '); }
            else sb.Append(f.Str("text"));
        }
        return (sb.ToString(), emotes);
    }

    public static List<string> FindLinks(string text)
    {
        var t = DotTrickRe.Replace(text ?? "", ".");
        var hosts = new List<string>();
        foreach (Match m in UrlRe.Matches(t))
        {
            var s = m.Value.ToLowerInvariant();
            s = Regex.Replace(s, "^(?:https?://)?(?:www\\.)?", "");
            var host = s.Split('/', '?', '#', ':')[0].TrimEnd('.', ',', '!', ')');
            if (host.Contains('.')) hosts.Add(host);
        }
        return hosts;
    }

    private static bool Allowed(JsonObject c, string host) =>
        c.StrList("links.allow").Select(a => a.Trim().ToLowerInvariant().TrimStart('.'))
            .Where(a => a != "")
            .Any(a => host == a || host.EndsWith("." + a));

    private static readonly Dictionary<char, char> Look = new()
    {
        ['a'] = 'а', ['b'] = 'в', ['c'] = 'с', ['e'] = 'е', ['h'] = 'н', ['k'] = 'к', ['m'] = 'м', ['o'] = 'о', ['p'] = 'р',
        ['t'] = 'т', ['x'] = 'х', ['y'] = 'у', ['3'] = 'з', ['0'] = 'о', ['4'] = 'ч', ['6'] = 'б', ['@'] = 'а', ['ё'] = 'е', ['u'] = 'и',
    };

    /// <summary>Для поиска слов: нижний регистр, латинские «двойники» → кириллица, всё лишнее — пробел.</summary>
    public static string Normalize(string s)
    {
        s = (s ?? "").ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        // латинские «двойники» заменяем только в словах, где есть кириллица («kaзино» → «казино», а «casino» остаётся)
        foreach (var word in Regex.Split(s, @"(\s+)"))
        {
            var hasCyr = word.Any(ch => ch is >= 'а' and <= 'я' or 'ё');
            foreach (var ch in word)
            {
                var x = ch;
                if (ch == 'ё') x = 'е';
                else if (hasCyr && Look.TryGetValue(ch, out var r)) x = r;
                sb.Append(char.IsLetterOrDigit(x) ? x : ' ');
            }
        }
        // «с.л.о.в.о» и «с л о в о» → «слово»
        var res = Regex.Replace(sb.ToString(), @"\b(\w) (?=\w\b)", "$1");
        return SpaceRe.Replace(res, " ").Trim();
    }

    public static string RuleTitle(string key) => Rules.FirstOrDefault(r => r.Key == key).Title ?? key;
    public static string ActionTitle(string action, int timeout) => action switch
    {
        "timeout" => "таймаут " + Dur(timeout),
        "delete" => "сообщение удалено",
        _ => "предупреждение",
    };
    private static string Dur(int s) =>
        s >= 86400 && s % 86400 == 0 ? $"{s / 86400} д" : s >= 3600 && s % 3600 == 0 ? $"{s / 3600} ч" : s >= 60 && s % 60 == 0 ? $"{s / 60} мин" : $"{s} сек";
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
