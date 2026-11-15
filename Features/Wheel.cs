using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Колесо фортуны для OBS (/wheel). Результат выбирает сервер (криптографический генератор с весами) ещё до начала
/// вращения — оверлей только красиво докручивает до заранее известного сектора. Запуски идут очередью.
/// Настройки и сами колёса — в разделе настроек «wheel» (резервные копии, профили, облако).
/// </summary>
public sealed class Wheel
{
    public const int MinSectors = 2, MaxSectors = 24, NameMax = 30;
    public static readonly string[] Bases = { "studio", "classic", "neon", "minimal", "retro" };
    public static readonly string[] Palette =
        { "#8b5cf6", "#ec4899", "#f59e0b", "#10b981", "#3b82f6", "#ef4444", "#14b8a6", "#f97316", "#a855f7", "#22c55e", "#eab308", "#06b6d4" };

    public sealed record Sector(string Name, int Weight, string Color, string Action, string Text, string AlertId, int Seconds);

    public sealed class Spin
    {
        public long Id;
        public string WheelId = "", WheelName = "", User = "", Login = "", UserId = "", Source = "";
        public List<Sector> Sectors = new();
        public int Result;
        public bool Test, Giveaway;
        public string Prize = "";
        public string RewardId = "", RedemptionId = "";
        public double StartedAt;      // секунды (ActionLog.Now); 0 — ещё в очереди
        public int DurationMs;
        public double Ts;
    }

    private readonly Hub _hub;
    private readonly List<Spin> _queue = new();
    private Spin _current;
    private long _seq;
    private double _lastViewerSpin;
    private string _limitKey = "";
    private readonly Dictionary<string, int> _perUser = new();
    private TaskCompletionSource<bool> _wake = new();      // в очереди появилось вращение
    private TaskCompletionSource<bool> _skipSig = new();   // «Пропустить» текущее

    public Wheel(Hub hub) => _hub = hub;

    private JsonObject Cfg => _hub.S.Obj("wheel") ?? new JsonObject();

    // ---------- колёса ----------
    public JsonArray Wheels => Cfg.Arr("wheels") ?? new JsonArray();

    public JsonNode Find(string id)
    {
        var list = Wheels;
        if (list.Count == 0) return null;
        if (string.IsNullOrEmpty(id)) id = Cfg.Str("active");
        return list.FirstOrDefault(w => w.Str("id") == id) ?? list[0];
    }

    private static string CleanColor(string c, int i) =>
        Regex.IsMatch(c ?? "", "^#[0-9a-fA-F]{6}$") ? c : Palette[i % Palette.Length];

    /// <summary>Сектора колеса с проверкой: 2–24 штуки, имя до 30 символов, вес 1–1000.</summary>
    public static List<Sector> SectorsOf(JsonNode wheel)
    {
        var res = new List<Sector>();
        var i = 0;
        foreach (var s in wheel?.Arr("sectors") ?? new JsonArray())
        {
            var name = s.Str("name").Trim();
            if (name == "") continue;
            if (name.Length > NameMax) name = name[..NameMax];
            var act = s.Str("action", "none");
            if (act is not ("none" or "chat" or "alert" or "timeout")) act = "none";
            res.Add(new Sector(name, Math.Clamp(s.Int("weight", 1), 1, 1000), CleanColor(s.Str("color"), i), act,
                s.Str("text").Trim() is var t && t.Length > 300 ? t[..300] : t, s.Str("alert_id"), Math.Clamp(s.Int("seconds", 60), 1, 1209600)));
            if (++i >= MaxSectors) break;
        }
        return res;
    }

    // ---------- случайный выбор ----------
    /// <summary>Честный выбор с весами (RandomNumberGenerator).</summary>
    public static int PickWeighted(IReadOnlyList<int> weights)
    {
        var total = weights.Sum();
        if (total <= 0) return 0;
        var r = RandomNumberGenerator.GetInt32(total);
        for (var i = 0; i < weights.Count; i++)
        {
            if (r < weights[i]) return i;
            r -= weights[i];
        }
        return weights.Count - 1;
    }

    // ---------- запуск ----------
    /// <summary>Ставит вращение в очередь. Возвращает текст ошибки (пусто — поставлено).</summary>
    public string Request(string source, string user, string login, string userId, string wheelId = "", bool test = false,
                          string rewardId = "", string redemptionId = "")
    {
        var c = Cfg;
        var w = Find(wheelId);
        if (w == null) return "колесо не настроено";
        var sectors = SectorsOf(w);
        if (sectors.Count < MinSectors) return $"в колесе «{w.Str("name")}» меньше {MinSectors} секторов";
        var viewer = source is "reward" or "chat" or "donation";
        if (viewer)
        {
            var now = ActionLog.Now;
            var cd = Math.Max(0, c.Int("cooldown", 10));
            if (cd > 0 && now - _lastViewerSpin < cd) return $"колесо можно крутить через {(int)(cd - (now - _lastViewerSpin)) + 1} сек";
            var limit = Math.Max(0, c.Int("per_user_limit", 0));
            if (_limitKey != _hub.Stream.Key) { _limitKey = _hub.Stream.Key; _perUser.Clear(); }
            if (limit > 0 && login != "" && _perUser.GetValueOrDefault(login) >= limit) return $"лимит — {limit} за стрим";
            if (_queue.Count >= Math.Clamp(c.Int("queue_max", 20), 1, 100)) return "очередь колеса заполнена";
            _lastViewerSpin = now;
            if (login != "") _perUser[login] = _perUser.GetValueOrDefault(login) + 1;
        }
        var spin = new Spin
        {
            Id = ++_seq, WheelId = w.Str("id"), WheelName = w.Str("name"), User = user, Login = login, UserId = userId, Source = source,
            Sectors = sectors, Result = PickWeighted(sectors.Select(s => s.Weight).ToList()), Test = test,
            RewardId = rewardId, RedemptionId = redemptionId, Ts = ActionLog.Now,
        };
        Enqueue(spin);
        _hub.Log.Add("event", $"Колесо «{spin.WheelName}»: запуск от {(user == "" ? SourceTitle(source) : user)}" + (test ? " (проверка)" : "") +
                              (_queue.Count > 1 ? $", в очереди {_queue.Count}" : ""));
        return "";
    }

    /// <summary>Режим «Колесом» в розыгрыше: победитель уже выбран из полного списка участников.</summary>
    public void SpinGiveaway(string prize, List<(string Name, int Tickets)> entries, string winner)
    {
        // на колесе — не больше 24 секторов: победитель плюс участники с наибольшим числом билетов, остальные случайно
        var others = entries.Where(e => e.Name != winner).OrderByDescending(e => e.Tickets).ThenBy(_ => RandomNumberGenerator.GetInt32(1 << 30))
            .Take(MaxSectors - 1).ToList();
        var pool = others.Append(entries.First(e => e.Name == winner)).OrderBy(_ => RandomNumberGenerator.GetInt32(1 << 30)).ToList();
        var sectors = pool.Select((e, i) => new Sector(e.Name.Length > NameMax ? e.Name[..NameMax] : e.Name, Math.Max(1, e.Tickets),
            Palette[i % Palette.Length], "none", "", "", 0)).ToList();
        var spin = new Spin
        {
            Id = ++_seq, WheelId = "giveaway", WheelName = prize, Source = "giveaway", Sectors = sectors,
            Result = pool.FindIndex(e => e.Name == winner), Giveaway = true, Prize = prize, Ts = ActionLog.Now,
        };
        Enqueue(spin);
        _hub.Log.Add("event", $"Розыгрыш «{prize}»: колесо из {sectors.Count} участников" + (entries.Count > sectors.Count ? $" (всего {entries.Count})" : ""));
    }

    private void Enqueue(Spin s)
    {
        _queue.Add(s);
        _wake.TrySetResult(true);
    }

    public void SkipCurrent()
    {
        if (_current == null) return;
        _skip = _current.StartedAt > 0 && ActionLog.Now < _current.StartedAt + _current.DurationMs / 1000.0;   // ещё крутится — без результата
        _skipSig.TrySetResult(true);
    }

    public void ClearQueue()
    {
        foreach (var s in _queue.ToList()) _ = Refund(s, "очередь колеса очищена");
        _queue.Clear();
    }

    public void Remove(long id)
    {
        var s = _queue.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        _queue.Remove(s);
        _ = Refund(s, "запуск убран из очереди");
    }

    private bool _skip;

    private async Task Refund(Spin s, string why)
    {
        if (s.RewardId == "") return;
        await _hub.Rewards.Complete(s.RewardId, s.RedemptionId, false);
        if (s.User != "") await _hub.Twitch.SendChat($"@{s.User}, {why} — баллы возвращены");
    }

    // ---------- цикл: по одному вращению ----------
    public async Task Run()
    {
        while (true)
        {
            if (_queue.Count == 0)
            {
                _wake = new TaskCompletionSource<bool>();
                await _wake.Task;
                continue;
            }
            var s = _queue[0];
            _queue.RemoveAt(0);
            var sec = Math.Clamp(Cfg.Num("spin_sec", 6), 5, 8);
            s.DurationMs = (int)(sec * 1000);
            s.StartedAt = ActionLog.Now + 0.4;   // чуть позже — оверлей успеет показаться
            _current = s;
            _skip = false;
            _skipSig = new TaskCompletionSource<bool>();
            await Task.WhenAny(Task.Delay(s.DurationMs + 400), _skipSig.Task);
            if (_skip)
            {
                _hub.Log.Add("skip", $"Колесо «{s.WheelName}»: вращение пропущено");
                await Refund(s, "вращение колеса пропущено");
                _current = null;
                continue;
            }
            await Finish(s);
            // победитель на экране — ещё несколько секунд, потом следующий запуск или скрытие
            var hold = Math.Clamp(Cfg.Int("hold_sec", 5), 2, 20);
            _skipSig = new TaskCompletionSource<bool>();
            await Task.WhenAny(Task.Delay(hold * 1000), _skipSig.Task);
            _current = null;
        }
    }

    private async Task Finish(Spin s)
    {
        var sec = s.Sectors[s.Result];
        var who = s.User == "" ? SourceTitle(s.Source) : s.User;
        var result = "";
        if (!s.Test && !s.Giveaway)
        {
            try { result = await RunAction(sec, s); }
            catch (Exception e) { result = "действие не выполнено: " + (e is TwitchException te ? te.Reason : e.Message); }
        }
        if (s.RewardId != "") await _hub.Rewards.Complete(s.RewardId, s.RedemptionId, true);
        var c = Cfg;
        var line = s.Giveaway ? $"🏆 Победитель розыгрыша «{s.Prize}»: @{sec.Name}! Поздравляем!"
            : Commands.Render(c.Str("chat_template", "🎡 {user} крутит колесо «{wheel}»: выпало «{result}»!"),
                new() { ["user"] = who, ["wheel"] = s.WheelName, ["result"] = sec.Name });
        if (!s.Test && (s.Giveaway || c.Bool("chat_result", true)) && line.Trim() != "")
            try { await _hub.Twitch.SendChat(line); } catch (Exception e) { _hub.Log.Add("skip", "Колесо: не удалось написать в чат — " + e.Message); }
        if (!s.Test) _hub.Events.ShowCustom("wheel", s.Giveaway ? "Розыгрыш" : "Колесо фортуны", who, s.Giveaway ? $"Победитель: {sec.Name}" : $"Выпало: {sec.Name}");
        _hub.Log.Add("event", $"Колесо «{s.WheelName}»: {who} → «{sec.Name}»" + (s.Test ? " (проверка)" : "") + (result != "" ? $" · {result}" : ""));
        AddHistory(s, sec, result);
    }

    private async Task<string> RunAction(Sector sec, Spin s)
    {
        var who = s.User == "" ? "стример" : s.User;
        switch (sec.Action)
        {
            case "chat":
                if (sec.Text == "") return "";
                await _hub.Twitch.SendChat(Commands.Render(sec.Text, new() { ["user"] = who, ["result"] = sec.Name }));
                return "сообщение в чат";
            case "alert":
                if (sec.AlertId == "") return "";
                _hub.Alerts.Play(sec.AlertId, who);
                return "алерт";
            case "timeout":
                if (s.UserId == "") return "таймаут пропущен: запуск не от зрителя";
                var tw = _hub.Twitch;
                await tw.Api(HttpMethod.Post, "/moderation/bans",
                    new() { ["broadcaster_id"] = tw.BroadcasterId, ["moderator_id"] = tw.Streamer.UserId },
                    new { data = new { user_id = s.UserId, duration = sec.Seconds, reason = $"Beg1nnnerOS: колесо «{s.WheelName}»" } });
                return $"таймаут {sec.Seconds} сек";
        }
        return "";
    }

    private void AddHistory(Spin s, Sector sec, string result)
    {
        var h = _hub.State.Data["wheel_history"] as JsonArray;
        if (h == null) { h = new JsonArray(); _hub.State.Data["wheel_history"] = h; }
        h.Insert(0, new JsonObject
        {
            ["ts"] = ActionLog.Now, ["wheel"] = s.WheelName, ["user"] = s.User, ["source"] = s.Source, ["result"] = sec.Name,
            ["action"] = result, ["test"] = s.Test, ["giveaway"] = s.Giveaway,
        });
        while (h.Count > 50) h.RemoveAt(h.Count - 1);
        _hub.State.Save();
    }

    public static string SourceTitle(string source) => source switch
    {
        "panel" => "панель", "dock" => "док-панель", "hotkey" => "горячая клавиша", "reward" => "награда",
        "donation" => "донат", "chat" => "команда в чате", "giveaway" => "розыгрыш", _ => source,
    };

    // ---------- триггеры ----------
    public async Task OnRedemption(string wheelId, string rewardId, string redId, string name, string login, string userId)
    {
        var err = Request("reward", name, login, userId, wheelId, false, rewardId, redId);
        if (err == "") return;
        await _hub.Rewards.Complete(rewardId, redId, false);
        _hub.Log.Add("skip", $"Колесо: награда от {login} не принята — {err}");
        await _hub.Twitch.SendChat($"@{name}, {err} — баллы возвращены");
    }

    public void OnDonation(string name, double amount)
    {
        var d = Cfg.Obj("donation");
        if (d == null || !d.Bool("enabled")) return;
        var min = Math.Max(1, d.Num("min", 100));
        if (amount < min) return;
        var err = Request("donation", name, "", "", d.Str("wheel"));
        if (err != "") _hub.Log.Add("skip", $"Колесо: донат от {name} — {err}");
    }

    public async Task<string> OnCommand(ChatMsg msg, string args)
    {
        var id = "";
        if (args != "")
        {
            var w = Wheels.FirstOrDefault(x => string.Equals(x.Str("name"), args.Trim(), StringComparison.OrdinalIgnoreCase));
            if (w != null) id = w.Str("id");
        }
        var err = Request("chat", msg.Name, msg.Login, msg.UserId, id);
        await Task.CompletedTask;
        return err == "" ? "" : $"@{msg.Name}, {err}";
    }

    /// <summary>Награды за баллы для колёс (для синхронизации с Twitch).</summary>
    public IEnumerable<(string Key, string Title, int Cost)> RewardsWanted()
    {
        foreach (var w in Wheels)
        {
            var r = w.Obj("reward");
            if (r == null || !r.Bool("enabled") || r.Str("title").Trim() == "") continue;
            yield return ("wheel:" + w.Str("id"), r.Str("title").Trim(), Math.Max(1, r.Int("cost", 500)));
        }
    }

    // ---------- состояние ----------
    private static object SpinObj(Spin s, bool full) => new
    {
        id = s.Id, wheel = s.WheelName, user = s.User, source = s.Source, test = s.Test, giveaway = s.Giveaway,
        sectors = full ? s.Sectors.Select(x => new { name = x.Name, weight = x.Weight, color = x.Color }) : null,
        result = full ? s.Result : -1,
        // оверлею — сколько уже прошло от начала вращения (по часам сервера), чтобы поздно открытый источник догнал
        elapsed_ms = s.StartedAt > 0 ? (int)Math.Round((ActionLog.Now - s.StartedAt) * 1000) : 0,
        duration_ms = s.DurationMs,
    };

    /// <summary>Для оверлея: текущее вращение (с результатом — он нужен, чтобы докрутить до нужного сектора).</summary>
    public object Overlay()
    {
        var c = Cfg;
        return new
        {
            spin = _current == null ? null : SpinObj(_current, true),
            sound = c.Bool("sound", true), volume = Math.Clamp(c.Int("volume", 60), 0, 100),
            theme = c.Str("theme", "studio"), title = c.Bool("show_title", true),
            custom_themes = _hub.Pro.ThemesFor("wheel"),
        };
    }

    public object State() => new
    {
        current = _current == null ? null : SpinObj(_current, false),
        current_result = _current != null && _current.StartedAt > 0 && ActionLog.Now >= _current.StartedAt + _current.DurationMs / 1000.0
            ? _current.Sectors[_current.Result].Name : "",
        queue = _queue.Select(s => SpinObj(s, false)).ToList(),
        history = _hub.State.Data["wheel_history"]?.DeepClone() ?? new JsonArray(),
        alerts = _hub.Alerts.Items.Select(a => new { id = a.Str("id"), name = a.Str("name") }),
    };
}
