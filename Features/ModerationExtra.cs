using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// «Модерация»: заявки на разбан (Twitch Helix) и подозрительные аккаунты —
/// отметки Twitch (подозрение на обход бана, общие баны с другими каналами, ручной мониторинг)
/// и совсем новые аккаунты, впервые пишущие в чат.
/// </summary>
public sealed class ModerationExtra
{
    public const string ScopeUnban = "moderator:manage:unban_requests";
    public const string ScopeSuspicious = "moderator:read:suspicious_users";

    private readonly Hub _hub;
    private readonly List<JsonObject> _suspicious = new();
    private readonly ConcurrentDictionary<string, bool> _checked = new();   // кого уже проверили по возрасту аккаунта
    private readonly HashSet<string> _dismissed = new();
    private List<JsonObject> _requests = new();
    private double _requestsAt;
    private string _requestsError = "";

    public ModerationExtra(Hub hub) => _hub = hub;
    private TwitchClient Tw => _hub.Twitch;
    private JsonObject Cfg => _hub.S.Obj("mod") ?? new JsonObject();
    private Dictionary<string, string> BM() => new() { ["broadcaster_id"] = Tw.BroadcasterId, ["moderator_id"] = Tw.Streamer.UserId };

    private static string Err(TwitchException e) => e.Status is 401 or 403
        ? "нет прав — нажмите «Войти в Twitch заново»" : "Twitch: " + e.Reason;

    // ---------- заявки на разбан ----------
    public async Task<object> Requests(bool refresh)
    {
        if (!Tw.Streamer.Ready) return new { ready = false, scope = false, items = new List<JsonObject>(), error = "" };
        var scope = Tw.Streamer.Scopes.Contains(ScopeUnban);
        if (scope && (refresh || ActionLog.Now - _requestsAt > 60))
        {
            try
            {
                var q = BM(); q["status"] = "pending"; q["first"] = "50";
                var js = await Tw.Api(HttpMethod.Get, "/moderation/unban_requests", q);
                _requests = (js["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(r => new JsonObject
                {
                    ["id"] = r.Str("id"), ["user_id"] = r.Str("user_id"), ["login"] = r.Str("user_login"), ["name"] = r.Str("user_name"),
                    ["text"] = r.Str("text"), ["created_at"] = r.Str("created_at"),
                }).ToList();
                _requestsError = "";
            }
            catch (TwitchException e) { _requestsError = Err(e); }
            _requestsAt = ActionLog.Now;
        }
        return new { ready = true, scope, items = _requests.Select(r => r.DeepClone()).ToList(), error = _requestsError };
    }

    public async Task<string> Resolve(string id, bool approve, string text)
    {
        if (string.IsNullOrEmpty(id)) throw new UserError("Не указана заявка");
        var q = BM(); q["unban_request_id"] = id; q["status"] = approve ? "approved" : "denied";
        if (!string.IsNullOrWhiteSpace(text)) q["resolution_text"] = text.Trim().Length > 500 ? text.Trim()[..500] : text.Trim();
        try { await Tw.Api(new HttpMethod("PATCH"), "/moderation/unban_requests", q); }
        catch (TwitchException e) { throw new UserError(e.Status == 400 ? "Заявка уже рассмотрена" : Err(e)); }
        var r = _requests.FirstOrDefault(x => x.Str("id") == id);
        _requests.RemoveAll(x => x.Str("id") == id);
        var who = r?.Str("name") ?? "зритель";
        _hub.Log.Add("mod", $"Заявка на разбан от {who}: {(approve ? "одобрена" : "отклонена")}");
        return approve ? $"{who} разбанен(а)" : $"Заявка {who} отклонена";
    }

    // ---------- подозрительные аккаунты ----------
    /// <summary>EventSub channel.suspicious_user.message / .update и channel.unban_request.*.</summary>
    public void OnEvent(string stype, JsonNode ev)
    {
        if (stype.StartsWith("channel.unban_request")) { _requestsAt = 0; return; }   // список обновится при следующем запросе
        var uid = ev.Str("user_id");
        if (uid == "") return;
        if (stype == "channel.suspicious_user.update")
        {
            var st = ev.Str("low_trust_status");
            lock (_suspicious)
                foreach (var s in _suspicious.Where(x => x.Str("user_id") == uid)) s["status"] = st;
            if (st == "no_treatment") lock (_suspicious) _suspicious.RemoveAll(x => x.Str("user_id") == uid && x.Str("source") == "twitch");
            return;
        }
        var reasons = new List<string>();
        foreach (var t in (ev["types"] as JsonArray ?? new JsonArray()).Select(x => J.AsStr(x)))
            reasons.Add(t switch
            {
                "ban_evader_detector" => "возможный обход бана",
                "shared_channel_ban" => "забанен на других каналах",
                "manually_added" => "отмечен модератором",
                _ => t,
            });
        var eva = ev.Str("ban_evasion_evaluation");
        if (eva is "likely" or "possible") reasons.Add(eva == "likely" ? "обход бана — вероятно" : "обход бана — возможно");
        Add(uid, ev.Str("user_login"), ev.Str("user_name"), "twitch", string.Join(", ", reasons.Distinct()), ev.Str("message.text"), ev.Str("low_trust_status"));
    }

    /// <summary>Каждое сообщение чата: новых зрителей проверяем по дате создания аккаунта (один раз за сеанс).</summary>
    public void OnChat(string uid, string login, string name, string text)
    {
        if (!Cfg.Bool("new_accounts", true) || uid == "" || !_checked.TryAdd(uid, true)) return;
        _hub.Loop.Start(async () =>
        {
            try
            {
                var u = TwitchClient.First(await Tw.Api(HttpMethod.Get, "/users", new() { ["id"] = uid }));
                if (u == null || !DateTime.TryParse(u.Str("created_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var created)) return;
                var days = (DateTime.UtcNow - created).TotalDays;
                var limit = Math.Clamp(Cfg.Int("new_account_days", 7), 1, 90);
                if (days < limit)
                    Add(uid, login, name, "age", days < 1 ? "аккаунт создан сегодня" : $"аккаунт создан {(int)days} дн. назад", text, "");
            }
            catch { }
        });
    }

    private void Add(string uid, string login, string name, string source, string reason, string text, string status)
    {
        lock (_suspicious)
        {
            if (_dismissed.Contains(uid)) return;
            var ex = _suspicious.FirstOrDefault(x => x.Str("user_id") == uid);
            if (ex != null)
            {
                ex["count"] = ex.Int("count") + 1; ex["text"] = Cut(text); ex["ts"] = ActionLog.Now;
                if (source == "twitch") { ex["source"] = "twitch"; ex["reason"] = reason; ex["status"] = status; }
                return;
            }
            _suspicious.Insert(0, new JsonObject
            {
                ["user_id"] = uid, ["login"] = login, ["name"] = name, ["source"] = source, ["reason"] = reason,
                ["text"] = Cut(text), ["status"] = status, ["ts"] = ActionLog.Now, ["count"] = 1,
            });
            if (_suspicious.Count > 50) _suspicious.RemoveRange(50, _suspicious.Count - 50);
        }
        _hub.Log.Add("mod", $"Подозрительный аккаунт: {name} — {reason}");
    }

    public List<JsonObject> Suspicious()
    {
        lock (_suspicious) return _suspicious.Select(x => (JsonObject)x.DeepClone()).ToList();
    }

    /// <summary>Действие с подозрительным аккаунтом: ban, timeout (10 мин) или dismiss (убрать из списка).</summary>
    public async Task<string> Act(string uid, string action)
    {
        JsonObject s;
        lock (_suspicious) s = _suspicious.FirstOrDefault(x => x.Str("user_id") == uid);
        var name = s?.Str("name") ?? "зритель";
        if (action != "dismiss")
        {
            var data = action == "ban"
                ? (object)new { user_id = uid, reason = "Beg1nnnerOS: подозрительный аккаунт" }
                : new { user_id = uid, duration = 600, reason = "Beg1nnnerOS: подозрительный аккаунт" };
            try { await Tw.Api(HttpMethod.Post, "/moderation/bans", BM(), new { data }); }
            catch (TwitchException e) { throw new UserError(e.Status == 400 ? "Зритель уже забанен" : Err(e)); }
            _hub.Log.Add("mod", $"{name}: {(action == "ban" ? "бан" : "таймаут 10 мин")} (подозрительный аккаунт)");
        }
        lock (_suspicious)
        {
            _suspicious.RemoveAll(x => x.Str("user_id") == uid);
            _dismissed.Add(uid);
        }
        return action switch { "ban" => $"{name} забанен(а)", "timeout" => $"{name}: таймаут 10 минут", _ => $"{name} убран(а) из списка" };
    }

    private static string Cut(string s) => s == null ? "" : s.Length > 200 ? s[..200] + "…" : s;
}
