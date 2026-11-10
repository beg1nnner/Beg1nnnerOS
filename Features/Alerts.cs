using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;
using NAudio.Wave;

namespace Beg1nnnerOS.Features;

/// <summary>Подключённый источник /alerts (WebSocket) с очередью отправки.</summary>
public sealed class AlertClient
{
    public readonly WebSocket Socket;
    /// <summary>Подключён из OBS (а не из предпросмотра в панели) — для индикаторов мастера настройки.</summary>
    public bool Obs;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AlertClient(WebSocket socket) { Socket = socket; }

    public async Task<bool> Send(string json)
    {
        await _lock.WaitAsync();
        try
        {
            if (Socket.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(json);
            await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch { return false; }
        finally { _lock.Release(); }
    }
}

/// <summary>МедиаАлерты за баллы канала (раздел 10).</summary>
public sealed class Alerts
{
    private static readonly Dictionary<string, (string[] Exts, long Limit)> Formats = new()
    {
        ["image"] = (new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }, 12L * 1024 * 1024),
        ["video"] = (new[] { ".mp4", ".webm" }, 50L * 1024 * 1024),
        ["audio"] = (new[] { ".wav", ".mp3", ".ogg" }, 12L * 1024 * 1024),
    };

    private readonly Hub _hub;
    public readonly JsonStore Store;
    public readonly HashSet<AlertClient> Clients = new();
    public readonly List<JsonObject> Pending = new();
    public JsonObject Current;
    private TaskCompletionSource<bool> _wake = new();
    private CancellationTokenSource _stop = new();
    private readonly Dictionary<string, double> _lastGlobal = new();
    private readonly Dictionary<string, double> _lastUser = new();

    public Alerts(Hub hub)
    {
        _hub = hub;
        Store = new JsonStore(Path.Combine(Config.Profile, "alerts.json"), new JsonObject { ["items"] = new JsonArray() });
    }

    public List<JsonObject> Items => ((JsonArray)Store.Data["items"]).OfType<JsonObject>().ToList();
    private JsonArray ItemsArray => (JsonArray)Store.Data["items"];
    private JsonObject Cfg => _hub.S.Obj("alerts");
    public JsonObject Get(string id) => Items.FirstOrDefault(a => a.Str("id") == id);

    public static string KindOf(string filename)
    {
        var ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
        foreach (var (k, f) in Formats)
            if (f.Exts.Contains(ext)) return k;
        return null;
    }

    /// <summary>Длительность файла: MediaFoundation для видео/аудио, разбор кадров для GIF (один цикл).</summary>
    public static double? ProbeDuration(string path)
    {
        try
        {
            if (Path.GetExtension(path).ToLowerInvariant() == ".gif") return GifDuration(File.ReadAllBytes(path));
            using var r = new MediaFoundationReader(path);
            var s = r.TotalTime.TotalSeconds;
            return s >= 0.2 ? s : null;
        }
        catch { return null; }
    }

    private static double? GifDuration(byte[] b)
    {
        // Сумма задержек из Graphic Control Extension (0x21 0xF9 0x04 … delay в сотых долях секунды)
        double total = 0;
        var frames = 0;
        for (var i = 0; i + 7 < b.Length; i++)
        {
            if (b[i] == 0x21 && b[i + 1] == 0xF9 && b[i + 2] == 0x04)
            {
                var delay = b[i + 4] | (b[i + 5] << 8);
                total += (delay < 2 ? 10 : delay) / 100.0;
                frames++;
                i += 7;
            }
        }
        return frames > 1 && total >= 0.2 ? total : null;
    }

    // ---------- файлы ----------
    public object SaveUpload(string filename, byte[] data)
    {
        var kind = KindOf(filename) ?? throw new UserError("Формат не поддерживается: PNG, JPG, GIF, WebP, MP4, WebM, WAV, MP3, OGG");
        var limit = Formats[kind].Limit;
        if (data.LongLength > limit) throw new UserError($"Файл больше {limit / 1024 / 1024} МБ");
        var name = Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(filename).ToLowerInvariant();
        var path = Path.Combine(Config.MediaDir, name);
        File.WriteAllBytes(path, data);
        double? dur = kind is "video" or "audio" || name.EndsWith(".gif") ? ProbeDuration(path) : null;
        return new { file = name, kind, duration = dur.HasValue ? Math.Round(dur.Value, 2) : (double?)null, original = filename };
    }

    private void CleanupFiles()
    {
        var used = new HashSet<string>();
        foreach (var a in Items)
            foreach (var f in new[] { a.Str("media"), a.Str("sound") })
                if (f != "") used.Add(f);
        try
        {
            foreach (var f in new DirectoryInfo(Config.MediaDir).GetFiles())
                if (!used.Contains(f.Name) && f.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)) f.Delete();
        }
        catch { }
    }

    // ---------- CRUD ----------
    public JsonObject Save(JsonNode a)
    {
        var name = a.Str("name").Trim();
        if (name == "" || name.Length > 60) throw new UserError("Название алерта: от 1 до 60 символов");
        var id = a.Str("id");
        if (id == "") id = Guid.NewGuid().ToString("N")[..10];
        if (Items.Any(o => o.Str("id") != id && string.Equals(o.Str("name"), name, StringComparison.OrdinalIgnoreCase)))
            throw new UserError("Алерт с таким названием уже есть");
        var media = a.Str("media");
        var sound = a.Str("sound");
        var text = a.Str("text");
        if (text.Length > 300) text = text[..300];
        if (media == "" && text == "" && sound == "") throw new UserError("Добавьте изображение/видео, текст или звук");
        foreach (var (f, kinds) in new[] { (media, new[] { "image", "video" }), (sound, new[] { "audio" }) })
            if (f != "" && (!kinds.Contains(KindOf(f)) || !File.Exists(Path.Combine(Config.MediaDir, f))))
                throw new UserError($"Файл {f} не найден или неверного типа");
        var rewardTitle = a.Str("reward_title").Trim();
        if (rewardTitle == "" || rewardTitle.Length > 45) throw new UserError("Название награды Twitch: от 1 до 45 символов");
        var musicTitles = _hub.S.Obj("music.rewards").Select(p => p.Value.Str("title").ToLowerInvariant());
        if (musicTitles.Contains(rewardTitle.ToLowerInvariant()))
            throw new UserError("Название награды совпадает с музыкальной наградой");
        if (Items.Any(o => o.Str("id") != id && string.Equals(o.Str("reward_title"), rewardTitle, StringComparison.OrdinalIgnoreCase)))
            throw new UserError("Такое название награды уже используется другим алертом");
        var mode = a.Str("duration_mode", "manual");
        var duration = a.Num("duration", 5);
        if (duration == 0) duration = 5;
        if (duration < 1 || duration > 3600) throw new UserError("Длительность: от 1 до 3600 секунд");
        if (mode == "file")
        {
            var fd = new List<double>();
            // Длительность из файла; если сервер не смог её прочитать (WebM, OGG) — берём измеренную браузером
            void Add(string f, string clientKey)
            {
                if (f == "") return;
                var k = KindOf(f);
                if (k is not ("video" or "audio") && !f.EndsWith(".gif")) return;
                var d = ProbeDuration(Path.Combine(Config.MediaDir, f)) ?? (a.Num(clientKey) > 0 ? a.Num(clientKey) : (double?)null);
                if (d != null) fd.Add(d.Value);
            }
            Add(media, "media_duration");
            Add(sound, "sound_duration");
            if (fd.Count == 0) throw new UserError("У неподвижной картинки или текста без звука нужна ручная длительность");
            duration = Math.Min(3600, fd.Max());
        }
        var cost = a.Int("cost");
        if (cost < 1) throw new UserError("Цена награды должна быть больше 0");
        var clean = new JsonObject
        {
            ["id"] = id, ["name"] = name, ["media"] = media, ["media_kind"] = media != "" ? KindOf(media) : null,
            ["sound"] = sound, ["text"] = text, ["duration_mode"] = mode, ["duration"] = Math.Round(duration, 2),
            ["random_pos"] = a.Bool("random_pos", true), ["x"] = a.Int("x"), ["y"] = a.Int("y"),
            ["scale"] = Math.Max(0.05, Math.Min(5.0, a.Num("scale", 1) == 0 ? 1 : a.Num("scale", 1))),
            ["volume"] = Math.Max(0, Math.Min(100, a.Int("volume", 80))),
            ["cooldown_global"] = Math.Max(0, a.Int("cooldown_global")),
            ["cooldown_user"] = Math.Max(0, a.Int("cooldown_user")),
            ["reward_title"] = rewardTitle, ["cost"] = cost, ["enabled"] = a.Bool("enabled", true),
        };
        var arr = ItemsArray;
        var replaced = false;
        for (var i = 0; i < arr.Count; i++)
            if (arr[i].Str("id") == id) { arr[i] = clean; replaced = true; break; }
        if (!replaced) arr.Add(clean);
        Store.Save();
        CleanupFiles();
        _hub.Rewards.ScheduleSync();
        return clean;
    }

    public void Delete(string id)
    {
        var arr = ItemsArray;
        for (var i = arr.Count - 1; i >= 0; i--)
            if (arr[i].Str("id") == id) arr.RemoveAt(i);
        Store.Save();
        CleanupFiles();
        _hub.Rewards.ScheduleSync();
    }

    public void SetEnabled(string id, bool enabled)
    {
        var a = Get(id);
        if (a == null) return;
        a["enabled"] = enabled;
        Store.Save();
        _hub.Rewards.ScheduleSync();
    }

    public void SetVolume(string id, int volume)
    {
        volume = Math.Max(0, Math.Min(100, volume));
        if (!string.IsNullOrEmpty(id))
        {
            var a = Get(id);
            if (a != null) { a["volume"] = volume; Store.Save(); }
        }
        else
        {
            Cfg["volume"] = volume;
            _hub.Settings.Save();
        }
        var vols = new Dictionary<string, int>();
        foreach (var a in Items) vols[a.Str("id")] = a.Int("volume");
        Broadcast(new { type = "volume", master = Cfg.Int("volume", 80), alerts = vols });
    }

    // ---------- показ ----------
    public void Broadcast(object msg)
    {
        var json = J.Serialize(msg);
        foreach (var c in Clients.ToList())
            _ = SendTo(c, json);
    }

    private async Task SendTo(AlertClient c, string json)
    {
        if (!await c.Send(json)) Clients.Remove(c);
    }

    private object Payload(JsonObject a)
    {
        var obj = (JsonObject)a.DeepClone();
        obj["media_url"] = a.Str("media") != "" ? "/media/" + a.Str("media") : "";
        obj["sound_url"] = a.Str("sound") != "" ? "/media/" + a.Str("sound") : "";
        return new { type = "show", alert = obj, master = Cfg.Int("volume", 80) };
    }

    public async Task OnRedemption(string id, string rewardId, string redId, string name, string login)
    {
        var a = Get(id);
        var rw = _hub.Rewards;
        if (a == null || !a.Bool("enabled") || !Cfg.Bool("enabled"))
        {
            await rw.Complete(rewardId, redId, false);
            return;
        }
        var now = ActionLog.Now;
        var cg = a.Int("cooldown_global");
        var cu = a.Int("cooldown_user");
        var gLeft = cg - (now - (_lastGlobal.TryGetValue(id, out var lg) ? lg : 0));
        var uLeft = cu - (now - (_lastUser.TryGetValue(id + "|" + login, out var lu) ? lu : 0));
        var left = Math.Max(cg > 0 ? gLeft : 0, cu > 0 ? uLeft : 0);
        if (left > 0)
        {
            await rw.Complete(rewardId, redId, false);
            _hub.Log.Add("skip", $"Алерт «{a.Str("name")}» от {login}: кулдаун {(int)left + 1} сек");
            await _hub.Twitch.SendChat($"@{name}, «{a.Str("name")}» будет доступен через {(int)left + 1} сек. Баллы возвращены");
            return;
        }
        _lastGlobal[id] = now;
        _lastUser[id + "|" + login] = now;
        Pending.Add(new JsonObject
        {
            ["alert_id"] = id, ["reward_id"] = rewardId, ["redemption_id"] = redId,
            ["user"] = name, ["login"] = login, ["ts"] = now,
        });
        _hub.Log.Add("alert", $"Алерт «{a.Str("name")}» от {login} поставлен в очередь");
        _wake.TrySetResult(true);
    }

    public async Task StopAll()
    {
        var items = Pending.ToList();
        Pending.Clear();
        _stop.Cancel();
        Broadcast(new { type = "stop" });
        foreach (var it in items)
            await _hub.Rewards.Complete(it.Str("reward_id"), it.Str("redemption_id"), false);
        _hub.Log.Add("alert", $"МедиаАлерты остановлены, очередь очищена ({items.Count} возвращено)");
    }

    private DateTime _waitSince = DateTime.MinValue;
    private bool _waitWarned;
    private const int WaitWarnSec = 5, WaitRefundSec = 90;

    /// <summary>Источник /alerts в OBS не открыт: предупреждаем в журнале, а через 90 с возвращаем баллы.</summary>
    private async Task WaitForOverlay()
    {
        if (_waitSince == DateTime.MinValue) { _waitSince = DateTime.UtcNow; _waitWarned = false; }
        var waited = (DateTime.UtcNow - _waitSince).TotalSeconds;
        if (!_waitWarned && waited >= WaitWarnSec)
        {
            _waitWarned = true;
            _hub.Log.Error($"МедиаАлерт ждёт показа: в OBS не открыт источник «Браузер» с адресом …/alerts (его можно скопировать в разделе «МедиаАлерты»). " +
                           $"Добавьте его в OBS, иначе через {WaitRefundSec} с баллы вернутся зрителю.");
        }
        if (waited >= WaitRefundSec)
        {
            var items = Pending.ToList();
            Pending.Clear();
            _waitSince = DateTime.MinValue;
            foreach (var it in items)
            {
                await _hub.Rewards.Complete(it.Str("reward_id"), it.Str("redemption_id"), false);
                var a = Get(it.Str("alert_id"));
                _hub.Log.Add("skip", $"Алерт «{a?.Str("name")}» от {it.Str("login")} не показан: источник /alerts в OBS не открыт. Баллы возвращены");
                try { await _hub.Twitch.SendChat($"@{it.Str("user")}, «{a?.Str("name")}» сейчас не может быть показан. Баллы возвращены"); }
                catch { }
            }
            return;
        }
        await Task.Delay(1000);
    }

    public async Task Run()
    {
        while (true)
        {
            if (Pending.Count == 0)
            {
                _waitSince = DateTime.MinValue;
                _wake = new TaskCompletionSource<bool>();
                await _wake.Task;
                continue;
            }
            if (Clients.Count == 0)
            {
                // Для платной очереди нужен подключённый источник /alerts
                await WaitForOverlay();
                continue;
            }
            if (_waitSince != DateTime.MinValue && _waitWarned) _hub.Log.Add("alert", "Источник /alerts подключился — показываю алерты из очереди");
            _waitSince = DateTime.MinValue;
            var item = Pending[0];
            Pending.RemoveAt(0);
            var a = Get(item.Str("alert_id"));
            if (a == null)
            {
                await _hub.Rewards.Complete(item.Str("reward_id"), item.Str("redemption_id"), false);
                continue;
            }
            _stop = new CancellationTokenSource();
            var cur = (JsonObject)item.DeepClone();
            cur["name"] = a.Str("name");
            Current = cur;
            Broadcast(Payload(a));
            // Подтверждение выкупа происходит при старте показа
            await _hub.Rewards.Complete(item.Str("reward_id"), item.Str("redemption_id"), true);
            _hub.Log.Add("alert", $"Показ «{a.Str("name")}» для {item.Str("login")}");
            try { await Task.Delay(TimeSpan.FromSeconds(a.Num("duration", 5) + 0.5), _stop.Token); }
            catch (OperationCanceledException) { }
            Broadcast(new { type = "end" });
            Current = null;
            await Task.Delay(400);
        }
    }
}
