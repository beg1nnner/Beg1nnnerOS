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
    /// <summary>Twitch ответил 403 на список наград: у канала нет баллов (нужен статус компаньона или партнёра).</summary>
    public bool NoPoints;
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

    /// <summary>
    /// Награды, созданные другим ключом приложения Twitch (например, раньше вход был со своими ключами, а теперь — по коду,
    /// или наоборот). Twitch не даёт их менять и возвращать за них баллы, но выкупы программа видит и выполняет.
    /// </summary>
    private HashSet<string> Foreign
    {
        get => new((_hub.State.Data["reward_foreign"] as JsonArray ?? new JsonArray()).Select(x => J.AsStr(x)));
        set => _hub.State.Data["reward_foreign"] = new JsonArray(value.Select(x => (JsonNode)x).ToArray());
    }
    public bool IsForeign(string rewardId) => Foreign.Contains(rewardId);
    public int ForeignCount => Foreign.Count;

    public string KeyFor(string rewardId) =>
        Ids.FirstOrDefault(p => J.AsStr(p.Value) == rewardId).Key;

    /// <summary>Идентификатор награды Twitch по ключу программы («vwheel:main:0» и т. п.); пусто — награды нет.</summary>
    public string IdFor(string key) => Ids.Str(key);

    /// <summary>Награда, которую должна держать на канале программа. Enabled/Paused/Cooldown/Color — для колеса зрителей.</summary>
    private sealed record Desired(string Title, int Cost, bool Input, string Prompt, bool Enabled = true, bool Paused = false,
                                  int Cooldown = 0, string Color = "", bool Full = false);

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
        foreach (var (key, title, cost) in _hub.Wheel.RewardsWanted())
            outp[key] = new Desired(title, cost, false, L.TE("Крутит колесо фортуны на стриме"));
        foreach (var r in _hub.ViewerWheel.RewardsWanted())
            outp[r.Key] = new Desired(r.Title, r.Cost, r.Input, L.TE(r.Prompt), r.Enabled, r.Paused, r.Cooldown, r.Color, true);
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
            NoPoints = e.Status == 403;
            Status = e.Status == 403 ? "канал не имеет доступа к баллам (нужен статус компаньона/партнёра)" : $"ошибка: {e.Reason}";
            log.Error($"Награды: {Status}");
            return;
        }
        NoPoints = false;
        var desired = GetDesired();
        var newIds = new JsonObject();
        var errors = 0;
        var foreign = new HashSet<string>();
        List<JsonNode> all = null;   // все награды канала — загружаются, только если понадобятся
        async Task<JsonNode> FindForeign(string title)
        {
            if (all == null)
            {
                try { all = await tw.ListRewards(false); }
                catch (TwitchException) { all = new List<JsonNode>(); }
            }
            return all.FirstOrDefault(r => !existing.ContainsKey(r.Str("id")) &&
                string.Equals(r.Str("title").Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        foreach (var (key, d) in desired)
        {
            var title = d.Title.Length > 45 ? d.Title[..45] : d.Title;
            var body = new Dictionary<string, object>
            {
                ["title"] = title, ["cost"] = Math.Max(1, d.Cost), ["is_enabled"] = d.Enabled,
                ["is_user_input_required"] = d.Input, ["prompt"] = d.Prompt,
            };
            if (d.Full)
            {
                body["is_global_cooldown_enabled"] = d.Cooldown > 0;
                if (d.Cooldown > 0) body["global_cooldown_seconds"] = d.Cooldown;
                if (d.Color != "") body["background_color"] = d.Color;
            }
            var rid = Ids.Str(key);
            try
            {
                if (rid != "" && existing.TryGetValue(rid, out var r))
                {
                    var differs = r.Str("title") != title || r.Int("cost") != Math.Max(1, d.Cost) || r.Bool("is_enabled") != d.Enabled ||
                                  r.Bool("is_user_input_required") != d.Input || r.Str("prompt") != d.Prompt;
                    if (d.Full)
                        differs = differs || r.Bool("is_paused") != d.Paused ||
                                  r.Bool("global_cooldown_setting.is_enabled") != d.Cooldown > 0 ||
                                  d.Cooldown > 0 && r.Int("global_cooldown_setting.global_cooldown_seconds") != d.Cooldown ||
                                  d.Color != "" && !string.Equals(r.Str("background_color"), d.Color, StringComparison.OrdinalIgnoreCase);
                    if (differs)
                    {
                        if (d.Full) body["is_paused"] = d.Paused;
                        await tw.UpdateReward(rid, body);
                    }
                    newIds[key] = rid;
                }
                else if (await FindForeign(title) is { } f)
                {
                    // награда с таким названием уже есть, но создана другим ключом приложения — используем её как есть
                    newIds[key] = f.Str("id");
                    foreign.Add(f.Str("id"));
                }
                else
                {
                    var created = await tw.CreateReward(body);
                    newIds[key] = created.Str("id");
                    log.Add("system", $"Создана награда «{title}»");
                    // при создании Twitch не принимает «на паузе» — ставим отдельным запросом
                    if (d.Full && d.Paused) await tw.UpdateReward(created.Str("id"), new { is_paused = true });
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
                if (oldKey.StartsWith("alert:") || oldKey.StartsWith("wheel:") || oldKey.StartsWith("vwheel:"))
                    await tw.DeleteReward(rid);
                else
                {
                    if (r.Bool("is_enabled")) await tw.UpdateReward(rid, new { is_enabled = false });
                    newIds[oldKey != "" ? oldKey : "orphan:" + rid] = rid;
                }
            }
            catch (TwitchException e) { log.Error($"Награда «{r.Str("title")}»: {e.Reason}"); }
        }
        var wasForeign = Foreign;
        _hub.State.Data["reward_ids"] = newIds;
        Foreign = foreign;
        _hub.State.Save();
        if (foreign.Count > 0 && !foreign.SetEquals(wasForeign))
            log.Add("skip", $"Награды {string.Join(", ", foreign.Select(id => "«" + all.First(r => r.Str("id") == id).Str("title") + "»"))} созданы другим способом входа в Twitch " +
                "(другим ключом приложения). Программа принимает их выкупы, но не может менять цену и возвращать баллы. " +
                "Чтобы всё работало полностью, удалите эти награды в панели Twitch («Баллы канала» → «Управление наградами») — программа сама создаст их заново.");
        Status = errors == 0 ? "синхронизировано" : $"синхронизировано с ошибками ({errors})";
        if (foreign.Count > 0) Status += $" · {foreign.Count} созданы другим входом — их баллы не возвращаются";
    }

    public async Task Complete(string rewardId, string redemptionId, bool ok)
    {
        if (string.IsNullOrEmpty(rewardId) || string.IsNullOrEmpty(redemptionId)) return;   // тестовый показ
        if (IsForeign(rewardId))
        {
            // Twitch не даёт отмечать выкупы чужих наград — запрос останется в «Очереди запросов» на Twitch
            if (!ok) _hub.Log.Add("skip", "Баллы не возвращены автоматически: награда создана другим способом входа. Верните их вручную на Twitch («Очередь запросов наград») или пересоздайте награду.");
            return;
        }
        try { await _hub.Twitch.SetRedemption(rewardId, redemptionId, ok ? "FULFILLED" : "CANCELED"); }
        catch (Exception e) { _hub.Log.Error($"Не удалось {(ok ? "подтвердить" : "вернуть баллы за")} выкуп: {e.Message}"); }
    }

    /// <summary>
    /// Подтвердить (ok) или вернуть баллы за покупку заказа музыки — с повторами: если Twitch недоступен или токен
    /// обновляется, ещё две попытки с паузой. Не вышло — запись в журнал, чтобы стример вернул баллы вручную.
    /// </summary>
    public async Task Settle(string rewardId, string redemptionId, bool ok, string who, string title, int cost)
    {
        if (string.IsNullOrEmpty(rewardId) || string.IsNullOrEmpty(redemptionId)) return;
        if (IsForeign(rewardId))
        {
            if (!ok) _hub.Log.Add("skip", $"Баллы не возвращены автоматически ({who}, «{title}», {cost}): награда создана другим способом входа. " +
                                          "Верните их вручную на Twitch («Очередь запросов наград») или пересоздайте награду.");
            return;
        }
        // при запуске программы Twitch может быть ещё не подключён — ждём до минуты
        for (var i = 0; i < 60 && !_hub.Twitch.Streamer.Ready; i++) await Task.Delay(1000);
        string last = "";
        foreach (var pause in new[] { 0, 3, 8 })
        {
            if (pause > 0) await Task.Delay(pause * 1000);
            try
            {
                await _hub.Twitch.SetRedemption(rewardId, redemptionId, ok ? "FULFILLED" : "CANCELED");
                return;
            }
            catch (Exception e) { last = e is TwitchException te ? te.Reason : e.Message; }
        }
        _hub.Log.Error(ok
            ? $"Не удалось подтвердить на Twitch заказ {who} «{title}» ({cost} баллов): {last}. Покупка осталась в «Очереди запросов наград» — отметьте её там"
            : $"Не удалось вернуть баллы {who} за «{title}» ({cost} баллов): {last}. Верните их вручную в «Очереди запросов наград» на Twitch");
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
        else if (key.StartsWith("wheel:"))
            await _hub.Wheel.OnRedemption(key[6..], rid, redId, name, login, ev.Str("user_id"));
        else if (key.StartsWith("vwheel:"))
        {
            // vwheel:{колесо}:{номер награды}
            var parts = key.Split(':');
            await _hub.ViewerWheel.OnRedemption(parts.Length > 1 ? parts[1] : "", parts.Length > 2 && int.TryParse(parts[2], out var n) ? n : 0, ev);
        }
        else
            await Complete(rid, redId, false);
    }

    // приоритет зрителя (VIP, подписка) — на 10 минут, чтобы не спрашивать Twitch при каждом заказе
    private readonly Dictionary<string, (int P, double Ts)> _prioCache = new();

    /// <summary>VIP и подписка проверяются одновременно и параллельно с поиском трека. Ошибка — приоритет 0.</summary>
    private async Task<int> PriorityFor(JsonNode ev, OrderTiming tm)
    {
        var m = _hub.S.Obj("music");
        var uid = ev.Str("user_id");
        bool vipOn = m.Bool("vip_priority"), subOn = m.Bool("sub_priority");
        if (!vipOn && !subOn || uid == "") return 0;
        var key = uid + (vipOn ? "v" : "") + (subOn ? "s" : "");
        if (_prioCache.TryGetValue(key, out var c) && ActionLog.Now - c.Ts < 600) { tm.Priority = 0; return c.P; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var vip = vipOn ? _hub.Twitch.IsVip(uid) : Task.FromResult(false);
            var sub = subOn ? _hub.Twitch.IsSubscriber(uid) : Task.FromResult(false);
            await Task.WhenAll(vip, sub);
            var p = vip.Result ? 2 : sub.Result ? 1 : 0;
            _prioCache[key] = (p, ActionLog.Now);
            if (_prioCache.Count > 2000) _prioCache.Clear();
            return p;
        }
        catch (Exception e)
        {
            _hub.Log.Add("skip", $"Не удалось проверить VIP/подписку: {(e is TwitchException te ? te.Reason : e.Message)}");
            return 0;
        }
        finally { tm.Priority = sw.Elapsed.TotalSeconds; }
    }

    private async Task MusicRedemption(string kind, JsonNode ev, string rid, string redId, int cost, string name, string login)
    {
        var music = _hub.Music;
        var stats = _hub.Stats;
        var sk = _hub.Stream.Key;
        if (kind == "skip")
        {
            var cur = music.Current;
            if (cur != null && music.Skip($"награда от {login}", login == cur.Login ? SkipBy.Requester : SkipBy.Reward))
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
        var tm = new OrderTiming();
        // приоритет проверяется одновременно с поиском трека, а не перед ним
        var priority = kind == "priority" ? Task.FromResult(3) : PriorityFor(ev, tm);
        if (!_hub.YtDlp.Ready || _hub.YtDlp.Installing)
            await _hub.Twitch.SendChat($"@{name}, готовлю плеер к первому запуску — заказ будет обработан примерно через минуту");
        var (ok, text, track) = await music.Request(ev.Str("user_input"), name, login, 0, false, (rid, redId, cost), priority, tm);
        // Принятый заказ НЕ подтверждается сразу: покупка остаётся «ожидающей» и подтверждается, когда трек
        // отыграет 30 секунд (Music.ConfirmPay). Отклонённый — возвращается сразу, как раньше.
        if (!ok) await Complete(rid, redId, false);
        // потраченные баллы учитываются в статистике при подтверждении (тип paid), а не при заказе
        stats.Music(sk, ok ? kind : Music.IsRepeatText(text) ? "repeat" : kind, ok ? "ok" : "rejected", login, name, track?.Title ?? ev.Str("user_input"), 0);
        _hub.Log.Add("music", $"Заказ от {login}: {text}");
        if (!ok) await _hub.Twitch.SendChat($"@{name}, заказ отклонён: {text}. Баллы возвращены");
        else if (_hub.S.Bool("music.announce")) await _hub.Twitch.SendChat($"@{name}, {text}");
    }
}
