using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>Сообщения бота о событиях Twitch и DonationAlerts (раздел 11).</summary>
public sealed class Events
{
    private static readonly Dictionary<string, Dictionary<string, string>> Demo = new()
    {
        ["donation"] = new() { ["user"] = "Зритель", ["amount"] = "300", ["currency"] = "RUB", ["message"] = "Отличный стрим!" },
        ["follow"] = new() { ["user"] = "Зритель" },
        ["sub"] = new() { ["user"] = "Зритель", ["details"] = "подарил(а) 5 подписок (Tier 1)", ["months"] = "3", ["count"] = "5", ["tier"] = "1" },
        ["cheer"] = new() { ["user"] = "Зритель", ["bits"] = "100", ["message"] = "Отличный стрим!" },
        ["reward"] = new() { ["user"] = "Зритель", ["reward"] = "Заказать музыку", ["cost"] = "500" },
        ["raid"] = new() { ["user"] = "ДругойКанал", ["viewers"] = "42" },
        ["hype"] = new() { ["event"] = "начался", ["level"] = "1", ["details"] = "Прогресс 150/1000" },
        ["poll"] = new() { ["event"] = "началось", ["title"] = "Во что играем?", ["details"] = "Варианты: A / B" },
        ["ad"] = new() { ["duration"] = "90" },
        ["online"] = new() { ["title"] = "Вечерний стрим", ["category"] = "Just Chatting" },
        ["update"] = new() { ["title"] = "Новый заголовок", ["category"] = "Minecraft" },
    };

    private readonly Hub _hub;
    private readonly Queue<string> _queue = new();
    private TaskCompletionSource<bool> _signal = new();
    private readonly Dictionary<string, (int Count, CancellationTokenSource Cts)> _gifts = new();

    public Events(Hub hub) { _hub = hub; }

    private JsonObject Cfg => _hub.S.Obj("events");
    public bool Paused => Cfg.Bool("paused");
    public int QueueCount => _queue.Count;

    public void SetPaused(bool paused)
    {
        Cfg["paused"] = paused;
        _hub.Settings.Save();
        if (paused) _queue.Clear();
        _hub.Log.Add("system", "Уведомления о событиях " + (paused ? "на паузе" : "возобновлены"));
    }

    public static string Tier(string t) => t switch { "1000" => "1", "2000" => "2", "3000" => "3", _ => t };

    public void Notify(string type, Dictionary<string, string> values)
    {
        _hub.Pro.OnEvent(type, values);   // статистика стримов и озвучка
        var et = Config.EventTypes.FirstOrDefault(e => e.Key == type);
        var title = et?.Title ?? type;
        _hub.Log.Add("event", $"{title}: " + string.Join(", ", values.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={p.Value}")));
        var tpl = Cfg.Obj("templates." + type);
        if (tpl == null || !tpl.Bool("enabled")) return;
        if (Paused) { _hub.Log.Add("skip", $"{title}: уведомления на паузе"); return; }
        var text = Regex.Replace(Commands.Render(tpl.Str("text"), values), "[ \t]{2,}", " ").Trim();
        if (text == "") return;
        _queue.Enqueue(text);
        _signal.TrySetResult(true);
    }

    public string Preview(string type, string text) =>
        Commands.Render(text, Demo.TryGetValue(type, out var d) ? d : new Dictionary<string, string>());

    public async Task Run()
    {
        while (true)
        {
            if (_queue.Count == 0)
            {
                _signal = new TaskCompletionSource<bool>();
                await _signal.Task;
                continue;
            }
            var text = _queue.Dequeue();
            if (Paused) continue;
            await _hub.Twitch.SendChat(text);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Math.Min(60, Cfg.Int("interval_sec", 3)))));
        }
    }

    /// <summary>Объединяет подарки одного дарителя и уровня в окне gift_merge_sec.</summary>
    public void Gift(string gifter, string tier, int count)
    {
        var window = Math.Max(0, Math.Min(30, Cfg.Num("gift_merge_sec", 5)));
        var key = gifter + "\u0001" + tier;
        var total = count;
        if (_gifts.TryGetValue(key, out var g))
        {
            total += g.Count;
            g.Cts.Cancel();
        }
        var cts = new CancellationTokenSource();
        _gifts[key] = (total, cts);
        _ = FlushLater(key, gifter, tier, window, cts.Token);
    }

    private async Task FlushLater(string key, string gifter, string tier, double window, CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(window), ct); }
        catch (OperationCanceledException) { return; }
        if (!_gifts.TryGetValue(key, out var g)) return;
        _gifts.Remove(key);
        var n = g.Count;
        var word = n == 1 ? "подписку" : (n % 10 >= 2 && n % 10 <= 4 && !(n % 100 >= 12 && n % 100 <= 14)) ? "подписки" : "подписок";
        Notify("sub", new() { ["user"] = gifter, ["details"] = $"подарил(а) {n} {word} (Tier {tier})", ["months"] = "", ["count"] = n.ToString(), ["tier"] = tier });
    }

    public Task Handle(string stype, JsonNode ev)
    {
        string S(string path) => ev.Str(path);
        switch (stype)
        {
            case "channel.follow":
                Notify("follow", new() { ["user"] = S("user_name") });
                break;
            case "channel.subscribe":
                if (ev.Bool("is_gift")) break;   // учитывается в channel.subscription.gift
                Notify("sub", new() { ["user"] = S("user_name"), ["details"] = "оформил(а) подписку", ["months"] = "1", ["count"] = "1", ["tier"] = Tier(S("tier")) });
                break;
            case "channel.subscription.message":
                var months = ev.Int("cumulative_months", 1);
                Notify("sub", new() { ["user"] = S("user_name"), ["details"] = $"продлил(а) подписку: {months} мес.", ["months"] = months.ToString(), ["count"] = "1", ["tier"] = Tier(S("tier")) });
                break;
            case "channel.subscription.gift":
                var gifter = ev.Bool("is_anonymous") || S("user_name") == "" ? "Аноним" : S("user_name");
                Gift(gifter, Tier(S("tier")), Math.Max(1, ev.Int("total", 1)));
                break;
            case "channel.cheer":
                Notify("cheer", new() { ["user"] = ev.Bool("is_anonymous") || S("user_name") == "" ? "Аноним" : S("user_name"), ["bits"] = S("bits"), ["message"] = S("message") });
                break;
            case "channel.raid":
                Notify("raid", new() { ["user"] = S("from_broadcaster_user_name"), ["viewers"] = S("viewers") });
                break;
            case "channel.ad_break.begin":
                Notify("ad", new() { ["duration"] = S("duration_seconds") });
                break;
            case "channel.hype_train.begin":
            case "channel.hype_train.end":
                var begin = stype.EndsWith("begin");
                var details = begin ? $"Прогресс {ev.Int("progress")}/{S("goal")}" : $"Итог: {ev.Int("total")}";
                Notify("hype", new() { ["event"] = begin ? "начался" : "завершился", ["level"] = ev.Int("level", 1).ToString(), ["details"] = details });
                break;
            case "channel.poll.begin":
                var choices = string.Join(" / ", (ev["choices"] as JsonArray ?? new JsonArray()).Select(c => c.Str("title")));
                Notify("poll", new() { ["event"] = "началось", ["title"] = S("title"), ["details"] = $"Варианты: {choices}" });
                break;
            case "channel.poll.end":
                if (S("status") != "completed" && S("status") != "terminated") break;
                var best = (ev["choices"] as JsonArray ?? new JsonArray()).OrderByDescending(c => c.Int("votes")).FirstOrDefault();
                var res = best != null ? $"Победил вариант «{best.Str("title")}» ({best.Int("votes")} голосов)" : "";
                Notify("poll", new() { ["event"] = "завершилось", ["title"] = S("title"), ["details"] = res });
                break;
        }
        return Task.CompletedTask;
    }
}

/// <summary>Состояние трансляции, уведомление о старте и автосообщение.</summary>
public sealed class StreamState
{
    private readonly Hub _hub;
    public bool Live;
    public string StreamId = "";
    public double? StartedAt;
    public string Title = "";
    public string Category = "";
    public bool Checked;
    private double _lastSample;

    public StreamState(Hub hub) { _hub = hub; }

    public string Key => Live && StreamId != "" ? StreamId : $"session-{(long)_hub.Started}";

    public string UptimeText() =>
        !Live || StartedAt == null ? "стрим не идёт" : Commands.HumanDuration(ActionLog.Now - StartedAt.Value);

    private static double ParseTs(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() / 1000.0;

    public async Task Run()
    {
        while (true)
        {
            if (_hub.Twitch.Streamer.Ready)
            {
                try { await Refresh(!Checked); }
                catch (TwitchException e) { _hub.Log.Error($"Не удалось получить состояние эфира: {e.Message}"); }
                catch (Exception e) { _hub.Log.Error($"Состояние эфира: {e.GetType().Name}: {e.Message}"); }
            }
            await Task.Delay(60_000);
        }
    }

    public async Task Refresh(bool startup = false)
    {
        var tw = _hub.Twitch;
        var s = await tw.GetStream();
        var wasLive = Live;
        var oldId = StreamId;
        if (s != null)
        {
            Live = true;
            StreamId = s.Str("id");
            StartedAt = ParseTs(s.Str("started_at"));
            Title = s.Str("title");
            Category = s.Str("game_name");
            // замер зрителей для статистики стримов — не чаще раза в 50 секунд
            if (ActionLog.Now - _lastSample >= 50)
            {
                _lastSample = ActionLog.Now;
                try { _hub.Stats.StreamSample(StreamId, StartedAt ?? ActionLog.Now, Title, Category, s.Int("viewer_count")); }
                catch (Exception e) { _hub.Log.Error("Статистика зрителей: " + e.Message); }
            }
        }
        else
        {
            if (wasLive && oldId != "") _hub.Stats.StreamEnded(oldId);
            Live = false;
            var ch = await tw.GetChannel();
            Title = ch.Str("title");
            Category = ch.Str("game_name");
        }
        Checked = true;
        if (Live && !wasLive) await WentLive(!startup);
    }

    private async Task WentLive(bool announce)
    {
        var st = _hub.State;
        _hub.Timers.OnLive();
        if (st.Data.Str("announced_stream") != StreamId)
        {
            st.Data["announced_stream"] = StreamId;
            st.Save();
            // Запуск приложения посреди эфира не создаёт повторного уведомления о старте
            if (announce) _hub.Events.Notify("online", new() { ["title"] = Title, ["category"] = Category });
        }
        var ap = _hub.S.Obj("autopost");
        if (ap.Bool("enabled") && ap.Str("text") != "" && st.Data.Str("autopost_stream") != StreamId)
        {
            if (await _hub.Twitch.SendChat(ap.Str("text")))
            {
                st.Data["autopost_stream"] = StreamId;
                st.Save();
                _hub.Log.Add("timer", "Автосообщение для этого стрима отправлено");
            }
        }
    }

    public async Task Handle(string stype, JsonNode ev)
    {
        if (stype == "stream.online")
        {
            for (var i = 0; i < 5; i++)
            {
                try { await Refresh(); } catch (TwitchException) { }
                if (Live) return;
                await Task.Delay(4000);
            }
            // API ещё не отдаёт стрим — используем данные события
            Live = true;
            StreamId = ev.Str("id");
            StartedAt = ev.Str("started_at") != "" ? ParseTs(ev.Str("started_at")) : ActionLog.Now;
            await WentLive(true);
        }
        else if (stype == "stream.offline")
        {
            if (StreamId != "") _hub.Stats.StreamEnded(StreamId);
            Live = false;
            _hub.Log.Add("system", "Трансляция завершена");
        }
        else if (stype == "channel.update")
        {
            var title = ev.Str("title");
            var cat = ev.Str("category_name");
            if (title != Title || cat != Category)
            {
                Title = title;
                Category = cat;
                _hub.Events.Notify("update", new() { ["title"] = title, ["category"] = cat });
            }
        }
    }
}
