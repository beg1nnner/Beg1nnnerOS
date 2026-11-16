using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// «Колесо зрителей» — режим колеса фортуны, в котором секторы появляются из покупок награды за баллы канала.
/// Вес сектора — сумма вложенных в него баллов. Отрисовка, темы и очередь вращений — общие с обычным колесом (Wheel).
/// Режимы наполнения: «Участники» (сектор — ник зрителя) и «Варианты» (сектор — текст, который ввёл зритель).
/// Состояние (секторы, вклады, обработанные покупки) хранится в viewer_wheel.json и пишется после каждой покупки.
/// </summary>
public sealed class ViewerWheel
{
    public const int MaxRewards = 3, TextMin = 2, TextMax = 40, MaxSectors = 500;

    public sealed class Contrib
    {
        public string Login = "", Name = "", UserId = "", Source = "reward", RedId = "", RewardId = "";
        public long Points;
        public double Ts;
        public bool Confirmed;
    }

    public sealed class VSector
    {
        public string Id = "", Key = "", Name = "", Similar = "";
        public long Adjust;
        public double Created;
        public bool NoAnswer;
        public List<Contrib> Contribs = new();
        public long Points => Math.Max(0, Contribs.Sum(c => c.Points) + Adjust);
    }

    public sealed class Pending
    {
        public string Id = "", Key = "", Text = "", Login = "", Name = "", UserId = "", RedId = "", RewardId = "";
        public long Points;
        public double Ts;
    }

    public sealed class VState
    {
        public string WheelId = "";
        public string Status = "idle";   // idle | open | closed | spinning | between | done
        public double OpenedAt, EndsAt, Extended, NextAt;
        public List<VSector> Sectors = new();
        public List<VSector> Out = new();      // выбывшие («На выбывание») и перевыбранные
        public List<Pending> Pending = new();
        public Dictionary<string, int> PerUser = new();     // login|номер награды → покупок за сбор
        public Dictionary<string, int> PerReward = new();   // номер награды → покупок за сбор
        public string LastName = "";
        public long LastPoints;
        public double LastTs;
        public int Round;
        public long StartTotal;
        public string WinnerId = "", PrevStatus = "";
        public long SpinSeq;
        public LinkedList<string> Seen = new();
        public HashSet<string> SeenSet = new();
    }

    private readonly Hub _hub;
    private readonly JsonStore _store;
    private readonly Dictionary<string, VState> _states = new();
    private readonly Dictionary<string, double> _lastChat = new();   // login → когда писал в чат
    private readonly List<(string WheelId, string User, string SectorId, long Points)> _chatBuf = new();
    private double _lastChatSent;

    public ViewerWheel(Hub hub)
    {
        _hub = hub;
        _store = new JsonStore(Path.Combine(Config.Profile, "viewer_wheel.json"), new JsonObject { ["wheels"] = new JsonObject(), ["history"] = new JsonArray() });
        Load();
    }

    // ---------- настройки ----------
    private JsonObject WheelCfg => _hub.S.Obj("wheel") ?? new JsonObject();

    public static bool IsViewer(JsonNode w) => w?.Str("type") == "viewers";

    public IEnumerable<JsonNode> Wheels => (WheelCfg.Arr("wheels") ?? new JsonArray()).Where(IsViewer);

    public JsonNode FindCfg(string id) => Wheels.FirstOrDefault(w => w.Str("id") == id);

    private static JsonObject Opt(JsonNode w) => w?.Obj("viewer") ?? new JsonObject();

    private static bool Participants(JsonNode w) => Opt(w).Str("mode", "participants") != "options";

    public static List<JsonNode> RewardsOf(JsonNode w) =>
        (Opt(w).Arr("rewards") ?? new JsonArray()).Where(r => r is JsonObject && r.Str("title").Trim() != "").Take(MaxRewards).ToList();

    public VState StateOf(string id)
    {
        if (!_states.TryGetValue(id, out var st)) _states[id] = st = new VState { WheelId = id };
        return st;
    }

    // ---------- хранение ----------
    private void Load()
    {
        foreach (var kv in _store.Data.Obj("wheels") ?? new JsonObject())
        {
            var d = kv.Value;
            var st = new VState
            {
                WheelId = kv.Key, Status = d.Str("status", "idle"), OpenedAt = d.Num("opened_at"), EndsAt = d.Num("ends_at"),
                Extended = d.Num("extended"), LastName = d.Str("last_name"), LastPoints = (long)d.Num("last_points"), LastTs = d.Num("last_ts"),
                Round = d.Int("round"), StartTotal = (long)d.Num("start_total"), WinnerId = d.Str("winner"),
            };
            // вылет во время вращения: само вращение потеряно, секторы на месте
            if (st.Status == "spinning") st.Status = d.Str("prev_status") == "between" ? "between" : "closed";
            foreach (var s in d.Arr("sectors") ?? new JsonArray()) st.Sectors.Add(ReadSector(s));
            foreach (var s in d.Arr("out") ?? new JsonArray()) st.Out.Add(ReadSector(s));
            foreach (var p in d.Arr("pending") ?? new JsonArray())
                st.Pending.Add(new Pending
                {
                    Id = p.Str("id"), Key = p.Str("key"), Text = p.Str("text"), Login = p.Str("login"), Name = p.Str("name"), UserId = p.Str("user_id"),
                    RedId = p.Str("red_id"), RewardId = p.Str("reward_id"), Points = (long)p.Num("points"), Ts = p.Num("ts"),
                });
            foreach (var u in d.Obj("per_user") ?? new JsonObject()) st.PerUser[u.Key] = J.AsInt(u.Value);
            foreach (var u in d.Obj("per_reward") ?? new JsonObject()) st.PerReward[u.Key] = J.AsInt(u.Value);
            foreach (var id in d.StrList("seen")) Remember(st, id);
            _states[kv.Key] = st;
        }
    }

    private static VSector ReadSector(JsonNode s)
    {
        var v = new VSector
        {
            Id = s.Str("id"), Key = s.Str("key"), Name = s.Str("name"), Similar = s.Str("similar"), Adjust = (long)s.Num("adjust"),
            Created = s.Num("created"), NoAnswer = s.Bool("no_answer"),
        };
        foreach (var c in s.Arr("contribs") ?? new JsonArray())
            v.Contribs.Add(new Contrib
            {
                Login = c.Str("login"), Name = c.Str("name"), UserId = c.Str("user_id"), Source = c.Str("source", "reward"), RedId = c.Str("red_id"),
                RewardId = c.Str("reward_id"), Points = (long)c.Num("points"), Ts = c.Num("ts"), Confirmed = c.Bool("confirmed"),
            });
        return v;
    }

    private static JsonObject WriteSector(VSector s)
    {
        var cs = new JsonArray();
        foreach (var c in s.Contribs)
            cs.Add(new JsonObject
            {
                ["login"] = c.Login, ["name"] = c.Name, ["user_id"] = c.UserId, ["source"] = c.Source, ["red_id"] = c.RedId,
                ["reward_id"] = c.RewardId, ["points"] = c.Points, ["ts"] = c.Ts, ["confirmed"] = c.Confirmed,
            });
        return new JsonObject
        {
            ["id"] = s.Id, ["key"] = s.Key, ["name"] = s.Name, ["similar"] = s.Similar, ["adjust"] = s.Adjust, ["created"] = s.Created,
            ["no_answer"] = s.NoAnswer, ["contribs"] = cs,
        };
    }

    /// <summary>Пишет состояние на диск — после каждой покупки и любого изменения (переживает перезапуск и вылет).</summary>
    private void Save()
    {
        var wheels = new JsonObject();
        foreach (var (id, st) in _states)
        {
            var sectors = new JsonArray(); foreach (var s in st.Sectors) sectors.Add(WriteSector(s));
            var outs = new JsonArray(); foreach (var s in st.Out) outs.Add(WriteSector(s));
            var pend = new JsonArray();
            foreach (var p in st.Pending)
                pend.Add(new JsonObject
                {
                    ["id"] = p.Id, ["key"] = p.Key, ["text"] = p.Text, ["login"] = p.Login, ["name"] = p.Name, ["user_id"] = p.UserId,
                    ["red_id"] = p.RedId, ["reward_id"] = p.RewardId, ["points"] = p.Points, ["ts"] = p.Ts,
                });
            var pu = new JsonObject(); foreach (var (k, v) in st.PerUser) pu[k] = v;
            var pr = new JsonObject(); foreach (var (k, v) in st.PerReward) pr[k] = v;
            wheels[id] = new JsonObject
            {
                ["status"] = st.Status, ["prev_status"] = st.PrevStatus, ["opened_at"] = st.OpenedAt, ["ends_at"] = st.EndsAt, ["extended"] = st.Extended,
                ["last_name"] = st.LastName, ["last_points"] = st.LastPoints, ["last_ts"] = st.LastTs, ["round"] = st.Round,
                ["start_total"] = st.StartTotal, ["winner"] = st.WinnerId, ["sectors"] = sectors, ["out"] = outs, ["pending"] = pend,
                ["per_user"] = pu, ["per_reward"] = pr, ["seen"] = new JsonArray(st.Seen.Select(x => (JsonNode)x).ToArray()),
            };
        }
        _store.Data["wheels"] = wheels;
        try { _store.Save(); }
        catch (Exception e) { _hub.Log.Error("Колесо зрителей: не удалось сохранить состояние — " + e.Message); }
    }

    private static void Remember(VState st, string redId)
    {
        if (redId == "" || !st.SeenSet.Add(redId)) return;
        st.Seen.AddLast(redId);
        while (st.Seen.Count > 5000) { st.SeenSet.Remove(st.Seen.First.Value); st.Seen.RemoveFirst(); }
    }

    private JsonArray History
    {
        get
        {
            if (_store.Data["history"] is not JsonArray h) { h = new JsonArray(); _store.Data["history"] = h; }
            return h;
        }
    }

    // ---------- сравнение вариантов ----------
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Ключ варианта: обрезка, нижний регистр, «ё» → «е», один пробел, без знаков по краям.</summary>
    public static string NormKey(string s)
    {
        s = Spaces.Replace((s ?? "").Trim().ToLowerInvariant().Replace('ё', 'е'), " ");
        return s.Trim(' ', '.', ',', '!', '?', ';', ':', '"', '\'', '«', '»', '“', '”', '-', '—', '(', ')', '*', '_', '~').Trim();
    }

    private static string CleanText(string s) => Spaces.Replace((s ?? "").Trim(), " ").Trim(' ', '.', ',', '!', '?', ';', ':', '"', '\'', '«', '»', '“', '”');

    private static int Lev(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 99;
        var d = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) d[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = d[0]; d[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }

    /// <summary>Похожий вариант (опечатка в одну-две буквы) — не объединяем молча, а помечаем.</summary>
    private static VSector FindSimilar(VState st, VSector s)
    {
        var k = s.Key.StartsWith("o:") ? s.Key[2..] : s.Key;
        if (k.Length < 4) return null;
        var limit = k.Length <= 7 ? 1 : 2;
        return st.Sectors.Where(x => x != s && x.Key.StartsWith("o:"))
            .Select(x => (x, d: Lev(k, x.Key[2..]))).Where(t => t.d > 0 && t.d <= limit)
            .OrderBy(t => t.d).ThenByDescending(t => t.x.Points).Select(t => t.x).FirstOrDefault();
    }

    // ---------- случайный выбор ----------
    private static double Rnd()
    {
        Span<byte> b = stackalloc byte[8];
        RandomNumberGenerator.Fill(b);
        return (BitConverter.ToUInt64(b) >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Честный выбор с весами (криптографический генератор).</summary>
    public static int Pick(IReadOnlyList<double> weights)
    {
        var total = weights.Sum();
        if (total <= 0) return RandomNumberGenerator.GetInt32(Math.Max(1, weights.Count));
        var r = Rnd() * total;
        for (var i = 0; i < weights.Count; i++)
        {
            if (r < weights[i]) return i;
            r -= weights[i];
        }
        return weights.Count - 1;
    }

    // ---------- проценты ----------
    public static string Pct(long part, long total)
    {
        if (total <= 0) return "0";
        var p = part * 100.0 / total;
        return p >= 10 ? Math.Round(p).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Math.Round(p, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long Total(VState st) => st.Sectors.Sum(s => s.Points);

    private string Who(Contrib c) => c.Name != "" ? c.Name : c.Login;

    // ---------- сбор ----------
    public void Open(string id)
    {
        var w = FindCfg(id) ?? throw new UserError("Колесо зрителей не найдено");
        var st = StateOf(id);
        if (st.Status is "spinning") throw new UserError("Колесо крутится — дождитесь результата");
        if (RewardsOf(w).Count == 0) throw new UserError("Задайте хотя бы одну награду за баллы — по ней зрители попадают на колесо", "wheel.viewer.rewards");
        if (st.Status == "done")
        {
            // новый сбор после завершения — колесо начинается заново (итог уже в истории)
            st.Sectors.Clear(); st.Out.Clear();
            st.Round = 0; st.WinnerId = ""; st.NextAt = 0; st.StartTotal = 0;
            st.LastName = ""; st.LastPoints = 0; st.LastTs = 0;
        }
        st.Status = "open";
        st.OpenedAt = ActionLog.Now;
        st.Extended = 0;
        var min = Math.Clamp(Opt(w).Int("timer_min", 0), 0, 240);
        st.EndsAt = min > 0 ? ActionLog.Now + min * 60 : 0;
        st.PerUser.Clear(); st.PerReward.Clear();
        Save();
        _hub.Rewards.ScheduleSync();
        _hub.Log.Add("event", $"Колесо зрителей «{w.Str("name")}»: сбор открыт" + (min > 0 ? $" на {min} мин" : ""));
        var r = RewardsOf(w);
        var how = string.Join(", ", r.Select(x => $"«{x.Str("title").Trim()}» ({x.Int("cost", 500)})"));
        if (Opt(w).Bool("chat_add", true))
            _ = Chat(w, Participants(w)
            ? $"Колесо «{w.Str("name")}»: участвуйте за баллы канала — {how}. Чем больше баллов, тем больше сектор"
            : $"Колесо «{w.Str("name")}»: предлагайте варианты за баллы канала — {how}. Чем больше баллов, тем больше шанс");
    }

    public void Close(string id, string why = "")
    {
        var w = FindCfg(id);
        var st = StateOf(id);
        if (st.Status != "open") return;
        st.Status = st.Sectors.Count > 0 ? "closed" : "idle";
        st.EndsAt = 0;
        Save();
        _hub.Rewards.ScheduleSync();
        _hub.Log.Add("event", $"Колесо зрителей «{w?.Str("name") ?? id}»: сбор закрыт{(why != "" ? " — " + why : "")}. Секторов: {st.Sectors.Count}");
        if (w != null && why == "время вышло")
            _ = Chat(w, $"Сбор на колесо «{w.Str("name")}» закрыт. Секторов: {st.Sectors.Count}");
    }

    /// <summary>Очистить колесо: всё убирается, за ещё не подтверждённые покупки баллы возвращаются.</summary>
    public async Task<int> Clear(string id)
    {
        var st = StateOf(id);
        if (st.Status == "spinning") throw new UserError("Колесо крутится — дождитесь результата");
        var w = FindCfg(id);
        var refunds = st.Sectors.Concat(st.Out).SelectMany(s => s.Contribs).Where(c => c.Source == "reward" && !c.Confirmed && c.RedId != "").ToList();
        var pend = st.Pending.ToList();
        _states[id] = new VState { WheelId = id, Seen = st.Seen, SeenSet = st.SeenSet };
        Save();
        _hub.Rewards.ScheduleSync();
        foreach (var c in refunds) await _hub.Rewards.Complete(c.RewardId, c.RedId, false);
        foreach (var p in pend) await _hub.Rewards.Complete(p.RewardId, p.RedId, false);
        _hub.Log.Add("event", $"Колесо зрителей «{w?.Str("name") ?? id}» очищено" + (refunds.Count + pend.Count > 0 ? $", баллы возвращены за {refunds.Count + pend.Count} покупок" : ""));
        return refunds.Count + pend.Count;
    }

    // ---------- покупки ----------
    private async Task Refund(JsonNode w, string rewardId, string redId, string name, string why)
    {
        await _hub.Rewards.Complete(rewardId, redId, false);
        _hub.Log.Add("skip", $"Колесо зрителей «{w?.Str("name") ?? "?"}»: покупка от {name} не принята — {why}");
        if (name != "") await _hub.Twitch.SendChat($"@{name}, {why} — баллы возвращены");
    }

    public async Task OnRedemption(string wheelId, int idx, JsonNode ev)
    {
        var rid = ev.Str("reward.id");
        var redId = ev.Str("id");
        var name = ev.Str("user_name");
        var login = ev.Str("user_login").ToLowerInvariant();
        var uid = ev.Str("user_id");
        var w = FindCfg(wheelId);
        var st = StateOf(wheelId);
        if (redId != "" && st.SeenSet.Contains(redId)) return;   // одна и та же покупка — один раз
        Remember(st, redId);
        Save();
        if (w == null) { await Refund(null, rid, redId, name, "это колесо удалено"); return; }
        var o = Opt(w);
        var rewards = RewardsOf(w);
        var cost = Math.Max(1, ev.Int("reward.cost", idx < rewards.Count ? rewards[idx].Int("cost", 1) : 1));
        if (st.Status != "open") { await Refund(w, rid, redId, name, "сбор на колесо сейчас закрыт"); return; }
        // защита
        if (o.Bool("use_blocklist", true) && _hub.S.StrList("music.blocked_users").Any(u => u.Trim().TrimStart('@').ToLowerInvariant() == login))
        { await Refund(w, rid, redId, name, "вы в списке блокировок канала"); return; }
        var rw = idx < rewards.Count ? rewards[idx] : null;
        var perUser = Math.Max(0, rw?.Int("per_user", 0) ?? 0);
        var ukey = login + "|" + idx;
        if (perUser > 0 && st.PerUser.GetValueOrDefault(ukey) >= perUser)
        { await Refund(w, rid, redId, name, $"лимит — {perUser} {L.Pl(perUser, "покупка", "покупки", "покупок")} этой награды за сбор"); return; }
        var total = Math.Max(0, rw?.Int("total", 0) ?? 0);
        if (total > 0 && st.PerReward.GetValueOrDefault(idx.ToString()) >= total)
        { await Refund(w, rid, redId, name, "награда закончилась — общий лимит за сбор исчерпан"); return; }
        var fdays = Math.Clamp(o.Int("follower_days", 0), 0, 3650);
        if (fdays > 0 && uid != _hub.Twitch.Streamer.UserId)
        {
            try
            {
                var at = await _hub.Twitch.GetFollowedAt(uid);
                if (string.IsNullOrEmpty(at) || !DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var since)
                    || (DateTime.UtcNow - since).TotalDays < fdays)
                { await Refund(w, rid, redId, name, $"колесо — для фолловеров от {fdays} {L.Pl(fdays, "дня", "дней", "дней")}"); return; }
            }
            catch (TwitchException e) { _hub.Log.Add("skip", $"Колесо зрителей: не удалось проверить фоллов {login}: {e.Reason}"); }
            if (st.Status != "open") { await Refund(w, rid, redId, name, "сбор на колесо сейчас закрыт"); return; }
        }
        var c = new Contrib { Login = login, Name = name, UserId = uid, Source = "reward", RedId = redId, RewardId = rid, Points = cost, Ts = ActionLog.Now };
        string key, text;
        if (Participants(w))
        {
            key = "u:" + login;
            text = name;
        }
        else
        {
            text = CleanText(ev.Str("user_input"));
            if (text.Length < TextMin || text.Length > TextMax)
            { await Refund(w, rid, redId, name, $"вариант — от {TextMin} до {TextMax} символов"); return; }
            var bad = _hub.Moderation.CheckText(text);
            if (bad != "") { await Refund(w, rid, redId, name, $"вариант не принят ({bad})"); return; }
            key = "o:" + NormKey(text);
            if (key.Length <= 3) { await Refund(w, rid, redId, name, $"вариант — от {TextMin} до {TextMax} символов"); return; }
        }
        st.PerUser[ukey] = st.PerUser.GetValueOrDefault(ukey) + 1;
        st.PerReward[idx.ToString()] = st.PerReward.GetValueOrDefault(idx.ToString()) + 1;
        var sec = st.Sectors.FirstOrDefault(s => s.Key == key);
        if (sec == null && !Participants(w) && o.Bool("approval"))
        {
            // новый вариант — сначала на проверку; пополнение одобренного варианта проверки не требует
            st.Pending.Add(new Pending
            {
                Id = Guid.NewGuid().ToString("N")[..10], Key = key, Text = text, Login = login, Name = name, UserId = uid,
                RedId = redId, RewardId = rid, Points = cost, Ts = ActionLog.Now,
            });
            Save();
            _hub.Log.Add("event", $"Колесо зрителей: вариант «{text}» от {name} ждёт проверки");
            return;
        }
        if (sec == null && st.Sectors.Count >= MaxSectors) { await Refund(w, rid, redId, name, "на колесе больше нет места"); return; }
        await Accept(w, st, key, text, c);
    }

    private async Task Accept(JsonNode w, VState st, string key, string text, Contrib c)
    {
        var o = Opt(w);
        var sec = st.Sectors.FirstOrDefault(s => s.Key == key);
        var isNew = sec == null;
        if (isNew)
        {
            sec = new VSector { Id = Guid.NewGuid().ToString("N")[..10], Key = key, Name = text.Length > TextMax ? text[..TextMax] : text, Created = ActionLog.Now };
            st.Sectors.Add(sec);
            var sim = key.StartsWith("o:") ? FindSimilar(st, sec) : null;
            if (sim != null) sec.Similar = sim.Id;
        }
        sec.Contribs.Add(c);
        if (c.Source == "reward" && !o.Bool("confirm_on_spin")) c.Confirmed = true;
        st.LastName = Who(c); st.LastPoints = c.Points; st.LastTs = ActionLog.Now;
        // продление таймера сбора
        var ext = Math.Clamp(o.Int("extend_sec", 0), 0, 600);
        if (st.EndsAt > 0 && ext > 0)
        {
            var max = Math.Clamp(o.Int("extend_max", 120), 0, 3600);
            var add = Math.Min(ext, Math.Max(0, max - st.Extended));
            st.EndsAt += add; st.Extended += add;
        }
        Save();
        if (c.Confirmed) await _hub.Rewards.Complete(c.RewardId, c.RedId, true);
        _hub.Log.Add("event", $"Колесо зрителей «{w.Str("name")}»: {Who(c)} +{c.Points} → «{sec.Name}» ({Pct(sec.Points, Total(st))}%)");
        if (c.Source != "manual" && o.Bool("chat_add", true)) _chatBuf.Add((st.WheelId, Who(c), sec.Id, c.Points));
    }

    // ---------- проверка вариантов ----------
    public async Task Approve(string id, string pendingId, bool ok)
    {
        var w = FindCfg(id) ?? throw new UserError("Колесо зрителей не найдено");
        var st = StateOf(id);
        var p = st.Pending.FirstOrDefault(x => x.Id == pendingId) ?? throw new UserError("Этот вариант уже рассмотрен");
        st.Pending.Remove(p);
        if (!ok)
        {
            Save();
            await Refund(w, p.RewardId, p.RedId, p.Name, $"вариант «{p.Text}» отклонён");
            return;
        }
        if (st.Status is "spinning" or "done")
        {
            Save();
            await Refund(w, p.RewardId, p.RedId, p.Name, "колесо уже разыграно");
            return;
        }
        // все ждущие проверки с тем же вариантом одобряются вместе
        var same = st.Pending.Where(x => x.Key == p.Key).ToList();
        foreach (var x in same) st.Pending.Remove(x);
        foreach (var x in same.Prepend(p))
            await Accept(w, st, x.Key, p.Text, new Contrib
            {
                Login = x.Login, Name = x.Name, UserId = x.UserId, Source = "reward", RedId = x.RedId, RewardId = x.RewardId, Points = x.Points, Ts = x.Ts,
            });
    }

    // ---------- действия над секторами ----------
    private VSector Sec(VState st, string sid) => st.Sectors.FirstOrDefault(s => s.Id == sid) ?? throw new UserError("Сектор не найден — обновите страницу");

    private static void NotSpinning(VState st)
    {
        if (st.Status == "spinning") throw new UserError("Колесо крутится — дождитесь результата");
    }

    public void Rename(string id, string sid, string name)
    {
        var w = FindCfg(id) ?? throw new UserError("Колесо зрителей не найдено");
        var st = StateOf(id); NotSpinning(st);
        var s = Sec(st, sid);
        name = CleanText(name);
        if (name.Length < 1 || name.Length > TextMax) throw new UserError($"Название сектора — от 1 до {TextMax} символов");
        if (s.Key.StartsWith("o:"))
        {
            var key = "o:" + NormKey(name);
            if (st.Sectors.Any(x => x != s && x.Key == key)) throw new UserError("Такой вариант уже есть — объедините секторы");
            s.Key = key;
        }
        s.Name = name;
        Save();
    }

    public void SetPoints(string id, string sid, long points)
    {
        var st = StateOf(id); NotSpinning(st);
        var s = Sec(st, sid);
        if (points < 1 || points > 100_000_000) throw new UserError("Сумма — от 1 до 100 000 000 баллов");
        s.Adjust = points - s.Contribs.Sum(c => c.Points);
        Save();
    }

    public void Merge(string id, string sid, string into)
    {
        var st = StateOf(id); NotSpinning(st);
        var s = Sec(st, sid);
        var t = Sec(st, into);
        if (s == t) return;
        t.Contribs.AddRange(s.Contribs);
        t.Contribs.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        t.Adjust += s.Adjust;
        st.Sectors.Remove(s);
        foreach (var x in st.Sectors.Where(x => x.Similar == s.Id)) x.Similar = x == t ? "" : t.Id;
        Save();
        _hub.Log.Add("event", $"Колесо зрителей: «{s.Name}» объединён с «{t.Name}»");
    }

    /// <summary>Сколько баллов сектора ещё можно вернуть (покупки, не подтверждённые на Twitch).</summary>
    private static (int Can, int Cannot) Refundable(VSector s) =>
        (s.Contribs.Count(c => c.Source == "reward" && !c.Confirmed && c.RedId != ""),
         s.Contribs.Count(c => c.Source == "reward" && (c.Confirmed || c.RedId == "")));

    public async Task<int> Delete(string id, string sid, bool refund)
    {
        var w = FindCfg(id);
        var st = StateOf(id); NotSpinning(st);
        var s = Sec(st, sid);
        st.Sectors.Remove(s);
        foreach (var x in st.Sectors.Where(x => x.Similar == s.Id)) x.Similar = "";
        Save();
        var n = 0;
        if (refund)
            foreach (var c in s.Contribs.Where(c => c.Source == "reward" && !c.Confirmed && c.RedId != ""))
            { await _hub.Rewards.Complete(c.RewardId, c.RedId, false); n++; }
        else
            foreach (var c in s.Contribs.Where(c => c.Source == "reward" && !c.Confirmed && c.RedId != ""))
                await _hub.Rewards.Complete(c.RewardId, c.RedId, true);
        _hub.Log.Add("event", $"Колесо зрителей «{w?.Str("name") ?? id}»: сектор «{s.Name}» удалён" + (n > 0 ? $", баллы возвращены за {n} покупок" : ""));
        return n;
    }

    public void AddManual(string id, string name, long points)
    {
        var w = FindCfg(id) ?? throw new UserError("Колесо зрителей не найдено");
        var st = StateOf(id); NotSpinning(st);
        if (st.Status == "done") throw new UserError("Колесо уже разыграно — очистите его или откройте новый сбор");
        name = CleanText(name);
        if (name.Length < 1 || name.Length > TextMax) throw new UserError($"Название сектора — от 1 до {TextMax} символов");
        if (points < 1 || points > 100_000_000) throw new UserError("Сумма — от 1 до 100 000 000 баллов");
        var key = Participants(w) ? "u:" + NormKey(name).Replace(" ", "") : "o:" + NormKey(name);
        if (st.Sectors.Count >= MaxSectors && st.Sectors.All(s => s.Key != key)) throw new UserError("На колесе больше нет места");
        if (st.Status == "idle") st.Status = "closed";
        _ = Accept(w, st, key, name, new Contrib { Name = _hub.Twitch.Streamer.Login, Login = "", Source = "manual", Points = points, Ts = ActionLog.Now, Confirmed = true });
    }

    // ---------- донаты ----------
    public void OnDonation(string name, double amount, string currency, string message)
    {
        foreach (var w in Wheels)
        {
            var o = Opt(w);
            if (!o.Bool("donation.enabled")) continue;
            var st = StateOf(w.Str("id"));
            if (st.Status != "open") continue;
            if (!string.Equals(currency, "RUB", StringComparison.OrdinalIgnoreCase))
            { _hub.Log.Add("skip", $"Колесо зрителей: донат от {name} в {currency} — учитываются только рубли"); continue; }
            var rate = Math.Clamp(o.Num("donation.rate", 10), 0.01, 100000);
            var pts = (long)Math.Round(amount * rate);
            if (pts < 1) continue;
            var c = new Contrib { Name = name, Login = name.ToLowerInvariant(), Source = "donation", Points = pts, Ts = ActionLog.Now, Confirmed = true };
            var msg = " " + NormKey(message) + " ";
            if (Participants(w))
            {
                var hit = st.Sectors.Where(s => msg.Contains(" " + s.Key[2..] + " ")).OrderByDescending(s => s.Key.Length).FirstOrDefault();
                var key = hit?.Key ?? "u:" + NormKey(name).Replace(" ", "");
                _ = Accept(w, st, key, hit?.Name ?? name, c);
            }
            else
            {
                var hit = st.Sectors.Where(s => msg.Contains(" " + s.Key[2..] + " ")).OrderByDescending(s => s.Key.Length).FirstOrDefault();
                if (hit != null) { _ = Accept(w, st, hit.Key, hit.Name, c); continue; }
                var text = CleanText(message);
                if (text.Length < TextMin || text.Length > TextMax || _hub.Moderation.CheckText(text) != "" || o.Bool("approval"))
                { _hub.Log.Add("skip", $"Колесо зрителей: донат от {name} — в сообщении нет варианта с колеса"); continue; }
                _ = Accept(w, st, "o:" + NormKey(text), text, c);
            }
        }
    }

    // ---------- чат ----------
    public void Seen(string login)
    {
        if (login == "") return;
        _lastChat[login.ToLowerInvariant()] = ActionLog.Now;
        if (_lastChat.Count > 5000)
            foreach (var k in _lastChat.OrderBy(p => p.Value).Take(1000).Select(p => p.Key).ToList()) _lastChat.Remove(k);
    }

    private async Task Chat(JsonNode w, string text)
    {
        try { await _hub.Twitch.SendChat(text); }
        catch (Exception e) { _hub.Log.Add("skip", "Колесо зрителей: не удалось написать в чат — " + e.Message); }
    }

    /// <summary>Сообщения о покупках — не чаще одного в 5 секунд, несколько покупок объединяются.</summary>
    private async Task FlushChat()
    {
        if (_chatBuf.Count == 0 || ActionLog.Now - _lastChatSent < 5) return;
        var items = _chatBuf.ToList();
        _chatBuf.Clear();
        _lastChatSent = ActionLog.Now;
        foreach (var grp in items.GroupBy(x => x.WheelId))
        {
            var w = FindCfg(grp.Key);
            if (w == null) continue;
            var st = StateOf(grp.Key);
            var total = Total(st);
            string Line((string WheelId, string User, string SectorId, long Points) x)
            {
                var s = st.Sectors.FirstOrDefault(y => y.Id == x.SectorId);
                if (s == null) return "";
                var tpl = Opt(w).Str("add_template").Trim();
                if (tpl == "") tpl = Participants(w) ? "{user} на колесе — шанс {chance}%" : "{user} добавил «{option}» — теперь {chance}%";
                return Commands.Render(tpl, new Dictionary<string, string>
                {
                    ["user"] = x.User, ["option"] = s.Name, ["chance"] = Pct(s.Points, total), ["points"] = s.Points.ToString(), ["added"] = x.Points.ToString(),
                });
            }
            var list = grp.ToList();
            if (list.Count == 1) { var l = Line(list[0]); if (l.Trim() != "") await Chat(w, l); continue; }
            // несколько покупок — одним сообщением
            var parts = list.GroupBy(x => x.SectorId).Select(g =>
            {
                var s = st.Sectors.FirstOrDefault(y => y.Id == g.Key);
                return s == null ? "" : $"«{s.Name}» {Pct(s.Points, total)}%";
            }).Where(x => x != "").ToList();
            var text = $"Колесо: +{list.Count} {L.Pl(list.Count, "покупка", "покупки", "покупок")} — " + string.Join(" · ", parts);
            if (text.Length > 480) text = text[..477] + "…";
            await Chat(w, text);
        }
    }

    // ---------- вращение ----------
    public string SpinRequest(string id, string source)
    {
        var w = FindCfg(id);
        if (w == null) return "колесо зрителей не найдено";
        var st = StateOf(id);
        if (st.Status == "spinning") return "колесо уже крутится";
        if (st.Status == "idle") return "на колесе пока нет секторов — откройте сбор";
        if (st.Sectors.Count < 2) return $"нужно хотя бы 2 сектора (сейчас {st.Sectors.Count})";
        if (st.Status == "open") Close(id, "вращение");
        var elim = Opt(w).Str("spin_mode", "normal") == "elimination";
        if (st.Status is "closed" or "done")
        {
            st.Round = 0;
            st.StartTotal = Total(st);
            if (st.Status == "done") { st.WinnerId = ""; }
        }
        st.Round++;
        var sectors = st.Sectors.ToList();
        var weights = sectors.Select(s => elim ? 1.0 / Math.Max(1, s.Points) : Math.Max(1, s.Points)).ToList();
        var pick = Pick(weights);
        st.PrevStatus = st.Status;
        st.Status = "spinning";
        st.NextAt = 0;
        Save();
        var sec = Math.Clamp(Opt(w).Num("spin_sec", 10), 5, 30);
        _hub.Wheel.EnqueueViewer(new Wheel.Spin
        {
            WheelId = id, WheelName = w.Str("name"), Source = source, ViewerId = id, ViewerElim = elim, ViewerRound = st.Round,
            Sectors = sectors.Select(s => new Wheel.Sector(s.Name, (int)Math.Min(s.Points, 1_000_000_000), "", "none", "", "", 0)).ToList(),
            SectorIds = sectors.Select(s => s.Id).ToList(), Result = pick, FixedSec = sec, Ts = ActionLog.Now,
        });
        _hub.Log.Add("event", $"Колесо зрителей «{w.Str("name")}»: вращение" + (elim ? $" на выбывание, круг {st.Round}" : "") + $" · секторов {sectors.Count}");
        return "";
    }

    public void Spin(string id, string source)
    {
        var err = SpinRequest(id, source);
        if (err != "") throw new UserError(char.ToUpper(err[0]) + err[1..]);
    }

    /// <summary>«Перевыбрать» (режим «Участники»): победитель не ответил — убираем его и крутим ещё раз.</summary>
    public void Reroll(string id)
    {
        var w = FindCfg(id) ?? throw new UserError("Колесо зрителей не найдено");
        var st = StateOf(id);
        if (st.Status != "done" || st.WinnerId == "") throw new UserError("Перевыбрать можно после победы");
        if (!Participants(w)) throw new UserError("Перевыбор — только в режиме «Участники»");
        var win = st.Sectors.FirstOrDefault(s => s.Id == st.WinnerId);
        if (win != null) { st.Sectors.Remove(win); win.NoAnswer = true; st.Out.Add(win); }
        st.WinnerId = "";
        st.Status = "closed";
        if (st.Sectors.Count < 2)
        {
            if (st.Sectors.Count == 1) { Save(); _ = Win(w, st, st.Sectors[0], "reroll"); return; }
            Save();
            throw new UserError("Других участников не осталось");
        }
        Save();
        _ = Chat(w, $"Перевыбор: {win?.Name} не ответил — крутим колесо ещё раз");
        Spin(id, "panel");
    }

    public void OnSpinSkipped(Wheel.Spin s)
    {
        var st = StateOf(s.ViewerId);
        if (st.Status != "spinning") return;
        st.Status = st.PrevStatus is "between" ? "between" : "closed";
        st.Round = Math.Max(0, st.Round - 1);
        Save();
    }

    public async Task OnSpinFinished(Wheel.Spin s)
    {
        var w = FindCfg(s.ViewerId);
        var st = StateOf(s.ViewerId);
        st.Status = st.PrevStatus == "between" ? "between" : "closed";
        var sid = s.Result < s.SectorIds.Count ? s.SectorIds[s.Result] : "";
        var sec = st.Sectors.FirstOrDefault(x => x.Id == sid);
        if (w == null || sec == null) { Save(); return; }
        var o = Opt(w);
        // при вращении подтверждаем покупки, которые ждали (настройка «подтверждать при вращении»)
        foreach (var c in st.Sectors.SelectMany(x => x.Contribs).Where(c => c.Source == "reward" && !c.Confirmed && c.RedId != "").ToList())
        { c.Confirmed = true; await _hub.Rewards.Complete(c.RewardId, c.RedId, true); }
        if (s.ViewerElim)
        {
            st.Sectors.Remove(sec);
            st.Out.Add(sec);
            if (st.Sectors.Count == 1) { await Win(w, st, st.Sectors[0], "elimination"); return; }
            st.Status = "between";
            if (o.Bool("auto_next")) st.NextAt = ActionLog.Now + Math.Clamp(o.Int("next_sec", 5), 2, 120);
            Save();
            _hub.Events.ShowCustom("wheel", "Колесо зрителей", sec.Name, $"Выбывает: {sec.Name}");
            _hub.Log.Add("event", $"Колесо зрителей «{w.Str("name")}»: «{sec.Name}» выбывает, осталось {st.Sectors.Count}");
            if (o.Bool("chat_result", true)) await Chat(w, $"«{sec.Name}» выбывает с колеса «{w.Str("name")}». Осталось: {st.Sectors.Count}");
            return;
        }
        await Win(w, st, sec, "spin");
    }

    private async Task Win(JsonNode w, VState st, VSector sec, string how)
    {
        var o = Opt(w);
        var total = st.StartTotal > 0 ? st.StartTotal : Total(st) + st.Out.Sum(x => x.Points);
        var chance = Pct(sec.Points, Math.Max(total, sec.Points));
        st.WinnerId = sec.Id;
        st.Status = "done";
        var removeWinner = how == "spin" && o.Str("after_spin", "keep") == "remove" && st.Sectors.Count > 2;
        Save();
        var tpl = o.Str("result_template").Trim();
        if (tpl == "") tpl = "Победил «{option}» — {chance}%, вложено {points}";
        var top = sec.Contribs.Where(c => c.Login != "").GroupBy(c => c.Login).OrderByDescending(g => g.Sum(c => c.Points)).FirstOrDefault();
        var line = Commands.Render(tpl, new Dictionary<string, string>
        {
            ["option"] = sec.Name, ["chance"] = chance, ["points"] = sec.Points.ToString(), ["wheel"] = w.Str("name"),
            ["user"] = top == null ? sec.Name : top.First().Name,
        });
        if (o.Bool("chat_result", true) && line.Trim() != "") await Chat(w, line);
        _hub.Events.ShowCustom("wheel", "Колесо зрителей", sec.Name, $"Победил: {sec.Name}");
        _hub.Log.Add("event", $"Колесо зрителей «{w.Str("name")}»: победил «{sec.Name}» — {chance}%, вложено {sec.Points}");
        AddHistory(w, st, sec, chance);
        if (Participants(w) && o.Bool("giveaway"))
        {
            var login = sec.Key.StartsWith("u:") ? sec.Key[2..] : "";
            var c = sec.Contribs.FirstOrDefault(x => x.Login == login);
            _hub.Pro.Giveaway.RecordWinner(login, c?.Name ?? sec.Name, c?.UserId ?? "", w.Str("name"));
        }
        if (removeWinner)
        {
            // разыграть следующее место: победитель уходит с колеса, остальные остаются
            st.Sectors.Remove(sec);
            st.Out.Add(sec);
            st.Status = "closed";
            st.WinnerId = "";
            Save();
        }
        _hub.Rewards.ScheduleSync();
    }

    private void AddHistory(JsonNode w, VState st, VSector win, string chance)
    {
        var all = st.Sectors.Concat(st.Out).ToList();
        History.Insert(0, new JsonObject
        {
            ["ts"] = ActionLog.Now, ["wheel"] = w.Str("name"), ["wheel_id"] = w.Str("id"), ["mode"] = Participants(w) ? "participants" : "options",
            ["elimination"] = Opt(w).Str("spin_mode") == "elimination", ["winner"] = win.Name, ["chance"] = chance, ["points"] = win.Points,
            ["sectors"] = all.Count, ["total"] = all.Sum(x => x.Points),
            ["contributors"] = all.SelectMany(x => x.Contribs).Where(c => c.Login != "").Select(c => c.Login).Distinct().Count(),
        });
        while (History.Count > 30) History.RemoveAt(History.Count - 1);
        Save();
    }

    // ---------- цикл: таймер сбора, автоматическое «Дальше», сообщения в чат ----------
    public async Task Run()
    {
        while (true)
        {
            await Task.Delay(1000);
            try
            {
                foreach (var st in _states.Values.ToList())
                {
                    if (st.Status == "open" && st.EndsAt > 0 && ActionLog.Now >= st.EndsAt) Close(st.WheelId, "время вышло");
                    if (st.Status == "between" && st.NextAt > 0 && ActionLog.Now >= st.NextAt)
                    {
                        st.NextAt = 0;
                        var err = SpinRequest(st.WheelId, "auto");
                        if (err != "") _hub.Log.Add("skip", "Колесо зрителей: " + err);
                    }
                }
                await FlushChat();
            }
            catch (Exception e) { _hub.Log.Error("Колесо зрителей: " + e.Message); }
        }
    }

    /// <summary>Выход из программы: открытый сбор закрывается, награды ставятся на паузу.</summary>
    public async Task PauseOnExit()
    {
        var ids = new List<string>();
        foreach (var st in _states.Values.Where(s => s.Status == "open"))
        {
            st.Status = st.Sectors.Count > 0 ? "closed" : "idle";
            st.EndsAt = 0;
            ids.Add(st.WheelId);
        }
        if (ids.Count == 0) return;
        Save();
        if (!_hub.Twitch.Streamer.Ready) return;
        foreach (var id in ids)
            for (var i = 0; i < MaxRewards; i++)
            {
                var rid = _hub.Rewards.IdFor(RewardKey(id, i));
                if (rid == "" || _hub.Rewards.IsForeign(rid)) continue;
                try { await _hub.Twitch.UpdateReward(rid, new { is_paused = true }); } catch { }
            }
    }

    // ---------- награды Twitch ----------
    public static string RewardKey(string wheelId, int i) => $"vwheel:{wheelId}:{i}";

    public sealed record WantedReward(string Key, string Title, int Cost, bool Input, string Prompt, bool Enabled, bool Paused, int Cooldown, string Color);

    /// <summary>
    /// Какие награды должны быть на канале. Сбор открыт — награды активны; закрыт или колесо крутится — на паузе;
    /// колесо разыграно или пустое — скрыты или удалены (настройка «после завершения»). Активных наград без сбора не остаётся.
    /// </summary>
    public IEnumerable<WantedReward> RewardsWanted()
    {
        foreach (var w in Wheels)
        {
            var o = Opt(w);
            var st = StateOf(w.Str("id"));
            var rewards = RewardsOf(w);
            var finished = st.Status is "idle" or "done";
            if (finished && o.Str("after_finish", "hide") == "delete") continue;
            for (var i = 0; i < rewards.Count; i++)
            {
                var r = rewards[i];
                var color = r.Str("color");
                if (!Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) color = "";
                var prompt = r.Str("prompt").Trim();
                if (prompt == "") prompt = Participants(w) ? "Участие в колесе: чем больше баллов, тем больше ваш сектор" : "Напишите вариант: игра, фильм или задание (2–40 символов)";
                yield return new WantedReward(RewardKey(w.Str("id"), i), r.Str("title").Trim(), Math.Max(1, r.Int("cost", 500)), !Participants(w),
                    prompt.Length > 200 ? prompt[..200] : prompt, !finished, st.Status != "open", Math.Clamp(r.Int("cooldown", 0), 0, 604800), color.ToUpperInvariant());
            }
        }
    }

    public string RewardsSig() => string.Join(";", RewardsWanted().Select(r => r.ToString()));

    // ---------- состояние для панели и оверлея ----------
    private object SectorObj(VState st, VSector s, long total, bool full)
    {
        var (can, cannot) = Refundable(s);
        return new
        {
            id = s.Id, name = s.Name, points = s.Points, chance = Pct(s.Points, total),
            contributors = s.Contribs.Select(c => c.Login != "" ? c.Login : c.Source + c.Name).Distinct().Count(),
            similar = s.Similar, similar_name = s.Similar == "" ? "" : st.Sectors.FirstOrDefault(x => x.Id == s.Similar)?.Name ?? "",
            refundable = can, not_refundable = cannot, no_answer = s.NoAnswer,
            contribs = full ? s.Contribs.OrderByDescending(c => c.Ts).Take(200).Select(c => new
            {
                user = c.Name != "" ? c.Name : c.Login, points = c.Points, ts = c.Ts, source = c.Source, confirmed = c.Confirmed,
            }).ToList() : null,
        };
    }

    public object State(string id)
    {
        var w = FindCfg(id);
        var st = StateOf(id);
        var total = Total(st);
        var win = st.Sectors.Concat(st.Out).FirstOrDefault(s => s.Id == st.WinnerId);
        double seenAgo = -1;
        if (win != null && win.Key.StartsWith("u:") && _lastChat.TryGetValue(win.Key[2..], out var t)) seenAgo = ActionLog.Now - t;
        var tw = _hub.Twitch;
        return new
        {
            id, exists = w != null, status = st.Status, participants = w == null || Participants(w),
            ends_in = st.Status == "open" && st.EndsAt > 0 ? Math.Max(0, (int)Math.Ceiling(st.EndsAt - ActionLog.Now)) : 0,
            next_in = st.Status == "between" && st.NextAt > 0 ? Math.Max(0, (int)Math.Ceiling(st.NextAt - ActionLog.Now)) : 0,
            total, round = st.Round,
            // порядок — как на колесе (по времени появления); таблица в панели сортирует по сумме сама
            sectors = st.Sectors.Select(s => SectorObj(st, s, total, true)).ToList(),
            overlay = Overlay(id),
            out_sectors = st.Out.Select(s => new { id = s.Id, name = s.Name, points = s.Points, no_answer = s.NoAnswer }).ToList(),
            pending = st.Pending.Select(p => new { id = p.Id, text = p.Text, user = p.Name, points = p.Points, ts = p.Ts }).ToList(),
            last = st.LastTs > 0 ? new { user = st.LastName, points = st.LastPoints, ts = st.LastTs } : null,
            winner = win == null ? null : new { id = win.Id, name = win.Name, points = win.Points, seen_ago = seenAgo },
            history = History.Where(h => h.Str("wheel_id") == id).Take(30).Select(h => h.DeepClone()).ToList(),
            rewards = new
            {
                status = _hub.Rewards.Status, no_points = _hub.Rewards.NoPoints, connected = tw.Streamer.Ready,
                scope_missing = tw.Streamer.Ready && tw.Streamer.MissingScopes().Contains("channel:manage:redemptions"),
                foreign = RewardsOf(w).Select((r, i) => _hub.Rewards.IdFor(RewardKey(id, i))).Count(x => x != "" && _hub.Rewards.IsForeign(x)),
            },
        };
    }

    /// <summary>Какое колесо зрителей сейчас на экране: выбранное, если в нём что-то идёт, иначе любое с открытым сбором.</summary>
    private (JsonNode W, VState St) Showing()
    {
        var active = WheelCfg.Str("active");
        bool Live(VState s) => s.Status is "open" or "closed" or "spinning" or "between" or "done";
        var a = FindCfg(active);
        if (a != null && Live(StateOf(active))) return (a, StateOf(active));
        foreach (var w in Wheels)
        {
            var st = StateOf(w.Str("id"));
            if (st.Status is "open" or "spinning" or "between") return (w, st);
        }
        return (null, null);
    }

    public object Overlay() { var (w, st) = Showing(); return w == null ? null : OverlayOf(w, st); }

    /// <summary>То же для конкретного колеса — предпросмотр в панели.</summary>
    public object Overlay(string id) { var w = FindCfg(id); return w == null ? null : OverlayOf(w, StateOf(id)); }

    private object OverlayOf(JsonNode w, VState st)
    {
        var o = Opt(w);
        var total = Total(st);
        var rewards = RewardsOf(w);
        var costs = rewards.Select(r => Math.Max(1, r.Int("cost", 500))).ToList();
        var label = rewards.Count == 0 ? "" : $"{rewards[0].Str("title").Trim()} — {string.Join(" / ", costs.Select(c => c.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))))} {L.Pl(costs[^1], "балл", "балла", "баллов")}";
        var win = st.Sectors.Concat(st.Out).FirstOrDefault(s => s.Id == st.WinnerId);
        return new
        {
            id = w.Str("id"), name = w.Str("name"), status = st.Status, label = st.Status == "open" ? label : "",
            show = o.Str("show", "collect") == "collect" && (st.Status == "open" || st.Status is "closed" or "between" or "done" && st.Sectors.Count > 0),
            ends_in = st.Status == "open" && st.EndsAt > 0 ? Math.Max(0, (int)Math.Ceiling(st.EndsAt - ActionLog.Now)) : 0,
            elimination = o.Str("spin_mode") == "elimination", left = st.Sectors.Count,
            sectors = st.Sectors.Select(s => new { id = s.Id, name = s.Name, weight = s.Points }).ToList(),
            top = st.Sectors.OrderByDescending(s => s.Points).Take(8).Select(s => new { name = s.Name, pct = Pct(s.Points, total) }).ToList(),
            last = st.LastTs > 0 && st.Status == "open" ? new { user = st.LastName, points = st.LastPoints } : null,
            winner = win?.Name ?? "",
        };
    }
}
