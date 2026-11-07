using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>Повторяющиеся таймеры сообщений (раздел 8) и разовые таймеры зрителей (раздел 9).</summary>
public sealed class Timers
{
    private const int MinSec = 5, MaxSec = 24 * 3600, OverdueLimit = 3600, PerUser = 5;
    private const string IdAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    private static readonly Regex PartRe = new(@"(\d+)([a-zа-яё]*)");
    private static readonly Random Rnd = new();

    private readonly Hub _hub;
    public readonly JsonStore Store;
    public readonly JsonStore VStore;
    private readonly Dictionary<string, double> _lastSent = new();
    private readonly Dictionary<string, int> _msgsSince = new();

    public Timers(Hub hub)
    {
        _hub = hub;
        Store = new JsonStore(System.IO.Path.Combine(Config.Profile, "timers.json"), new JsonObject { ["repeating"] = new JsonArray() });
        VStore = new JsonStore(System.IO.Path.Combine(Config.Profile, "viewer_timers.json"), new JsonObject { ["items"] = new JsonArray() });
        var now = ActionLog.Now;
        var before = Viewer.Count;
        RemoveViewerWhere(t => now - t.Num("due") > OverdueLimit);
        if (Viewer.Count != before) VStore.Save();
    }

    public JsonArray Repeating => (JsonArray)Store.Data["repeating"];
    public JsonArray Viewer => (JsonArray)VStore.Data["items"];

    private void RemoveViewerWhere(Func<JsonNode, bool> pred)
    {
        var arr = Viewer;
        for (var i = arr.Count - 1; i >= 0; i--)
            if (pred(arr[i])) arr.RemoveAt(i);
    }

    /// <summary>15м, 30с, 1ч30м, 10m, 45s, 2h, 90 (минуты) → секунды.</summary>
    public static int? ParseDuration(string token)
    {
        token = (token ?? "").Trim().ToLowerInvariant();
        var parts = PartRe.Matches(token).Cast<Match>().ToList();
        if (parts.Count == 0 || string.Concat(parts.Select(p => p.Value)) != token) return null;
        var total = 0;
        foreach (var p in parts)
        {
            var n = int.Parse(p.Groups[1].Value);
            var unit = p.Groups[2].Value;
            int mult;
            if (unit == "" && parts.Count == 1) mult = 60;
            else if (unit.StartsWith("с") || unit.StartsWith("s")) mult = 1;
            else if (unit.StartsWith("м") || unit.StartsWith("m")) mult = 60;
            else if (unit.StartsWith("ч") || unit.StartsWith("h")) mult = 3600;
            else return null;
            total += n * mult;
        }
        return total;
    }

    // ---------- повторяющиеся ----------
    public JsonObject SaveRepeating(JsonNode t)
    {
        var name = t.Str("name").Trim();
        var text = t.Str("text").Trim();
        if (name.Length > 60) name = name[..60];
        if (text.Length > 500) text = text[..500];
        if (name == "" || text == "") throw new UserError("Укажите название и текст таймера");
        var interval = t.Int("interval", 15);
        if (interval < 1 || interval > 1440) throw new UserError("Интервал: от 1 до 1440 минут");
        var minMsgs = t.Int("min_messages", 0);
        if (minMsgs < 0 || minMsgs > 1000) throw new UserError("Минимум сообщений: от 0 до 1000");
        var id = t.Str("id");
        if (id == "") id = Guid.NewGuid().ToString("N")[..10];
        var clean = new JsonObject
        {
            ["id"] = id, ["name"] = name, ["text"] = text, ["interval"] = interval,
            ["min_messages"] = minMsgs, ["enabled"] = t.Bool("enabled", true),
        };
        var arr = Repeating;
        var replaced = false;
        for (var i = 0; i < arr.Count; i++)
            if (arr[i].Str("id") == id) { arr[i] = clean; replaced = true; break; }
        if (!replaced) arr.Add(clean);
        Store.Save();
        return clean;
    }

    public void DeleteRepeating(string id)
    {
        var arr = Repeating;
        for (var i = arr.Count - 1; i >= 0; i--)
            if (arr[i].Str("id") == id) arr.RemoveAt(i);
        Store.Save();
    }

    public void OnChatMessage()
    {
        foreach (var t in Repeating)
        {
            var id = t.Str("id");
            _msgsSince[id] = (_msgsSince.TryGetValue(id, out var n) ? n : 0) + 1;
        }
    }

    public void OnLive()
    {
        var now = ActionLog.Now;
        foreach (var t in Repeating)
        {
            _lastSent[t.Str("id")] = now;
            _msgsSince[t.Str("id")] = 0;
        }
    }

    public object NextInfo()
    {
        if (!_hub.Stream.Live) return null;
        var now = ActionLog.Now;
        string bestName = null;
        double bestLeft = double.MaxValue;
        var bestMsgs = 0;
        foreach (var t in Repeating)
        {
            if (!t.Bool("enabled")) continue;
            var id = t.Str("id");
            var left = t.Int("interval") * 60 - (now - (_lastSent.TryGetValue(id, out var ls) ? ls : now));
            var msgsLeft = Math.Max(0, t.Int("min_messages") - (_msgsSince.TryGetValue(id, out var m) ? m : 0));
            if (left < bestLeft) { bestLeft = left; bestName = t.Str("name"); bestMsgs = msgsLeft; }
        }
        if (bestName == null) return null;
        return new { name = bestName, seconds = (int)Math.Max(0, bestLeft), messages_left = bestMsgs };
    }

    public async Task Run()
    {
        while (true)
        {
            await Task.Delay(1000);
            try
            {
                await TickViewer();
                await TickRepeating();
            }
            catch (Exception e) { _hub.Log.Error($"Таймеры: {e.Message}"); }
        }
    }

    private async Task TickRepeating()
    {
        if (!_hub.Stream.Live || !_hub.Twitch.SenderReady()) return;
        var now = ActionLog.Now;
        foreach (var t in Repeating.ToList())
        {
            if (!t.Bool("enabled")) continue;
            var id = t.Str("id");
            if (!_lastSent.ContainsKey(id)) _lastSent[id] = now;
            if (now - _lastSent[id] < t.Int("interval") * 60) continue;
            if ((_msgsSince.TryGetValue(id, out var m) ? m : 0) < t.Int("min_messages")) continue;
            if (await _hub.Twitch.SendChat(t.Str("text")))
            {
                _lastSent[id] = ActionLog.Now;
                _msgsSince[id] = 0;
                _hub.Log.Add("timer", $"Таймер «{t.Str("name")}» отправлен");
            }
            else
                _lastSent[id] = now - t.Int("interval") * 60 + 30;   // повтор через 30 сек
        }
    }

    // ---------- разовые ----------
    private string NewId()
    {
        var used = new HashSet<string>(Viewer.Select(t => t.Str("id")));
        while (true)
        {
            var id = new string(Enumerable.Range(0, 4).Select(_ => IdAlphabet[Rnd.Next(IdAlphabet.Length)]).ToArray());
            if (!used.Contains(id)) return id;
        }
    }

    public string CreateViewer(ChatMsg msg, string args)
    {
        var parts = args.Split((char[])null, 2, StringSplitOptions.RemoveEmptyEntries);
        var sec = parts.Length > 0 ? ParseDuration(parts[0]) : null;
        if (sec == null) return $"{msg.Name}, формат: !timer 15м текст (с — секунды, м — минуты, ч — часы)";
        if (sec < MinSec || sec > MaxSec) return $"{msg.Name}, время таймера — от 5 секунд до 24 часов";
        var mine = Viewer.Count(t => t.Str("login") == msg.Login);
        if (mine >= PerUser && !msg.IsMod) return $"{msg.Name}, у вас уже {PerUser} активных таймеров";
        var text = parts.Length > 1 ? parts[1].Trim() : "";
        if (text.Length > 300) text = text[..300];
        var id = NewId();
        Viewer.Add(new JsonObject
        {
            ["id"] = id, ["login"] = msg.Login, ["name"] = msg.Name, ["user_id"] = msg.UserId, ["text"] = text,
            ["due"] = ActionLog.Now + sec.Value, ["created"] = ActionLog.Now,
        });
        VStore.Save();
        return $"{msg.Name}, таймер {id} установлен на {Commands.HumanDuration(sec.Value)}";
    }

    public string ListViewer(ChatMsg msg)
    {
        var items = Viewer.Where(t => msg.IsMod || t.Str("login") == msg.Login).OrderBy(t => t.Num("due")).Take(8).ToList();
        if (items.Count == 0) return $"{msg.Name}, активных таймеров нет";
        var now = ActionLog.Now;
        var outp = items.Select(t =>
        {
            var who = msg.IsMod && t.Str("login") != msg.Login ? $" ({t.Str("name")})" : "";
            return $"{t.Str("id")}{who}: через {Commands.HumanDuration(t.Num("due") - now)}";
        });
        return $"{msg.Name}, таймеры: " + string.Join("; ", outp);
    }

    public string CancelViewer(ChatMsg msg, string args)
    {
        var tid = args.Trim() == "" ? "" : args.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        if (tid == "") return $"{msg.Name}, укажите ID: !canceltimer ID";
        var t = Viewer.FirstOrDefault(x => x.Str("id") == tid);
        if (t == null) return $"{msg.Name}, таймер с ID «{tid}» не найден";
        if (t.Str("login") != msg.Login && !msg.IsMod) return $"{msg.Name}, это не ваш таймер";
        Cancel(tid);
        return $"{msg.Name}, таймер {tid} отменён";
    }

    public void Cancel(string id)
    {
        RemoveViewerWhere(t => t.Str("id") == id);
        VStore.Save();
    }

    private async Task TickViewer()
    {
        var now = ActionLog.Now;
        var due = Viewer.Where(t => t.Num("due") <= now).Select(t => t.DeepClone()).ToList();
        foreach (var t in due)
        {
            if (now - t.Num("due") > OverdueLimit) { Cancel(t.Str("id")); continue; }
            if (!_hub.Twitch.SenderReady()) continue;
            var text = t.Str("text") != "" ? $"@{t.Str("name")}, напоминание: {t.Str("text")}" : $"@{t.Str("name")}, время таймера вышло!";
            if (await _hub.Twitch.SendChat(text))
            {
                Cancel(t.Str("id"));
                _hub.Log.Add("timer", $"Разовый таймер {t.Str("id")} для {t.Str("login")} доставлен");
            }
        }
    }
}
