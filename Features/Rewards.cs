using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>Синхронизация управляемых наград Twitch и обработка выкупов (музыка и МедиаАлерты).</summary>
public sealed class Rewards
{
    private const string MusicPrompt = "Ссылка YouTube / Яндекс / Spotify или «исполнитель — название»";
    private readonly Hub _hub;
    public string Status = "не синхронизировано";
    private bool _running, _pending;

    public Rewards(Hub hub) { _hub = hub; }

    private JsonObject Ids
    {
        get
        {
            if (_hub.State.Data["reward_ids"] is not JsonObject o)
            {
                o = new JsonObject();
                _hub.State.Data["reward_ids"] = o;
            }
            return o;
        }
    }

    public string KeyFor(string rewardId) =>
        Ids.FirstOrDefault(p => J.AsStr(p.Value) == rewardId).Key;

    private sealed record Desired(string Title, int Cost, bool Input, string Prompt);

    private Dictionary<string, Desired> GetDesired()
    {
        var outp = new Dictionary<string, Desired>();
        var m = _hub.S.Obj("music");
        if (m.Bool("rewards_enabled"))
            foreach (var kv in m.Obj("rewards"))
                if (kv.Value.Bool("enabled"))
                    outp["music:" + kv.Key] = new Desired(L.TE(kv.Value.Str("title")), kv.Value.Int("cost"),
                        kv.Key is "order" or "priority", kv.Key == "skip" ? L.TE("Пропускает текущий трек") : L.TE(MusicPrompt));
        if (_hub.S.Bool("alerts.enabled"))
            foreach (var a in _hub.Alerts.Items)
                if (a.Bool("enabled"))
                {
                    var prompt = a.Str("text");
                    outp["alert:" + a.Str("id")] = new Desired(a.Str("reward_title"), a.Int("cost"), false,
                        prompt.Length > 200 ? prompt[..200] : prompt);
                }
        return outp;
    }

    public void ScheduleSync() => _hub.Loop.Start(Sync);

    public async Task Sync()
    {
        if (!_hub.Twitch.Streamer.Ready) { Status = "нет входа стримера"; return; }
        if (_running) { _pending = true; return; }
        _running = true;
        try
        {
            do
            {
                _pending = false;
                await SyncOnce();
            } while (_pending);
        }
        finally { _running = false; }
    }

    private async Task SyncOnce()
    {
        var tw = _hub.Twitch;
        var log = _hub.Log;
        Dictionary<string, JsonNode> existing;
        try
        {
            existing = (await tw.ListRewards()).ToDictionary(r => r.Str("id"), r => r);
        }
        catch (TwitchException e)
        {
            Status = e.Status == 403 ? "канал не имеет доступа к баллам (нужен статус компаньона/партнёра)" : $"ошибка: {e.Reason}";
            log.Error($"Награды: {Status}");
            return;
        }
        var desired = GetDesired();
        var newIds = new JsonObject();
        var errors = 0;
        foreach (var (key, d) in desired)
        {
            var title = d.Title.Length > 45 ? d.Title[..45] : d.Title;
            var body = new Dictionary<string, object>
            {
                ["title"] = title, ["cost"] = Math.Max(1, d.Cost), ["is_enabled"] = true,
                ["is_user_input_required"] = d.Input, ["prompt"] = d.Prompt,
            };
            var rid = Ids.Str(key);
            try
            {
                if (rid != "" && existing.TryGetValue(rid, out var r))
                {
                    if (r.Str("title") != title || r.Int("cost") != Math.Max(1, d.Cost) || !r.Bool("is_enabled") ||
                        r.Bool("is_user_input_required") != d.Input || r.Str("prompt") != d.Prompt)
                        await tw.UpdateReward(rid, body);
                    newIds[key] = rid;
                }
                else
                {
                    var created = await tw.CreateReward(body);
                    newIds[key] = created.Str("id");
                    log.Add("system", $"Создана награда «{title}»");
                }
            }
            catch (TwitchException e)
            {
                errors++;
                log.Error($"Награда «{title}»: {e.Reason}");
            }
        }
        var used = new HashSet<string>(newIds.Select(p => J.AsStr(p.Value)));
        foreach (var (rid, r) in existing)
        {
            if (used.Contains(rid)) continue;
            var oldKey = Ids.FirstOrDefault(p => J.AsStr(p.Value) == rid).Key ?? "";
            try
            {
                if (oldKey.StartsWith("alert:"))
                    await tw.DeleteReward(rid);
                else
                {
                    if (r.Bool("is_enabled")) await tw.UpdateReward(rid, new { is_enabled = false });
                    newIds[oldKey != "" ? oldKey : "orphan:" + rid] = rid;
                }
            }
            catch (TwitchException e) { log.Error($"Награда «{r.Str("title")}»: {e.Reason}"); }
        }
        _hub.State.Data["reward_ids"] = newIds;
        _hub.State.Save();
        Status = errors == 0 ? "синхронизировано" : $"синхронизировано с ошибками ({errors})";
    }

    public async Task Complete(string rewardId, string redemptionId, bool ok)
    {
        try { await _hub.Twitch.SetRedemption(rewardId, redemptionId, ok ? "FULFILLED" : "CANCELED"); }
        catch (Exception e) { _hub.Log.Error($"Не удалось {(ok ? "подтвердить" : "вернуть баллы за")} выкуп: {e.Message}"); }
    }

    // ---------- выкупы ----------
    public async Task OnRedemption(JsonNode ev)
    {
        var rid = ev.Str("reward.id");
        var redId = ev.Str("id");
        var key = KeyFor(rid);
        var name = ev.Str("user_name");
        var login = ev.Str("user_login");
        _hub.Events.Notify("reward", new() { ["user"] = name, ["reward"] = ev.Str("reward.title"), ["cost"] = ev.Str("reward.cost") });
        var status = ev.Str("status");
        if (key == null)
        {
            // Награда с названием как у алерта/заказа музыки, но не связанная с программой —
            // скорее всего, её создали вручную на Twitch или связь потерялась.
            var title = ev.Str("reward.title");
            if (GetDesired().Values.Any(d => string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase)))
                _hub.Log.Error($"Награда «{title}» выкуплена, но она не связана с программой (создана вручную на Twitch?). " +
                               "Удалите её на Twitch и нажмите «Синхронизировать с Twitch» в разделе «Награды».");
            await _hub.Pro.OnRedemption(ev);   // участие в розыгрыше
            return;
        }
        if (status != "" && status != "unfulfilled") return;
        var cost = ev.Int("reward.cost");
        if (key.StartsWith("music:"))
            await MusicRedemption(key[6..], ev, rid, redId, cost, name, login);
        else if (key.StartsWith("alert:"))
            await _hub.Alerts.OnRedemption(key[6..], rid, redId, name, login);
        else
            await Complete(rid, redId, false);
    }

    private async Task<int> PriorityFor(JsonNode ev)
    {
        var m = _hub.S.Obj("music");
        var uid = ev.Str("user_id");
        try
        {
            if (m.Bool("vip_priority") && await _hub.Twitch.IsVip(uid)) return 2;
            if (m.Bool("sub_priority") && await _hub.Twitch.IsSubscriber(uid)) return 1;
        }
        catch (TwitchException e) { _hub.Log.Add("skip", $"Не удалось проверить VIP/подписку: {e.Reason}"); }
        return 0;
    }

    private async Task MusicRedemption(string kind, JsonNode ev, string rid, string redId, int cost, string name, string login)
    {
        var music = _hub.Music;
        var stats = _hub.Stats;
        var sk = _hub.Stream.Key;
        if (kind == "skip")
        {
            var cur = music.Current;
            if (cur != null && music.Skip($"награда от {login}"))
            {
                await Complete(rid, redId, true);
                stats.Music(sk, "skip", "ok", login, name, cur.Title, cost);
            }
            else
            {
                await Complete(rid, redId, false);
                stats.Music(sk, "skip", "rejected", login, name, "", 0);
                await _hub.Twitch.SendChat($"@{name}, сейчас ничего не играет — баллы возвращены");
            }
            return;
        }
        var priority = kind == "priority" ? 3 : await PriorityFor(ev);
        if (!_hub.YtDlp.Ready || _hub.YtDlp.Installing)
            await _hub.Twitch.SendChat($"@{name}, готовлю плеер к первому запуску — заказ будет обработан примерно через минуту");
        var (ok, text, track) = await music.Request(ev.Str("user_input"), name, login, priority, false, (rid, redId, cost));
        await Complete(rid, redId, ok);
        stats.Music(sk, kind, ok ? "ok" : "rejected", login, name, track?.Title ?? ev.Str("user_input"), ok ? cost : 0);
        _hub.Log.Add("music", $"Заказ от {login}: {text}");
        if (!ok) await _hub.Twitch.SendChat($"@{name}, заказ отклонён: {text}. Баллы возвращены");
        else if (_hub.S.Bool("music.announce")) await _hub.Twitch.SendChat($"@{name}, {text}");
    }
}
