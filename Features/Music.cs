using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

public sealed class Track
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
    public string Vid = "";
    public string Title = "";
    public string PageUrl = "";
    public int Duration;
    public string Requester = "";
    public string Login = "";
    public int Priority;
    public string Source = "youtube";
    public long Views = -1;   // просмотры на YouTube (-1 — неизвестно)
    public (string RewardId, string RedemptionId, int Cost)? Redemption;
    public bool Fallback;   // трек из резервного плейлиста (играет, когда заказов нет)
    public double ResumeAt;   // с какой секунды начать (восстановление после вылета)

    public object Public() => new
    {
        id = Id, vid = Vid, title = Title, page_url = PageUrl, duration = Duration, requester = Requester,
        login = Login, priority = Priority, source = Source, paid = Redemption != null, views = Views, fallback = Fallback,
    };
}

public sealed class ResolveException : Exception
{
    public ResolveException(string message) : base(message) { }
}

/// <summary>Музыка: поиск (YouTube / Яндекс / Spotify), очередь с приоритетами, локальный плеер.</summary>
public sealed class Music
{
    private static readonly Regex YtRe = new(@"^https?://(www\.|m\.|music\.)?(youtube\.com|youtu\.be)/", RegexOptions.IgnoreCase);
    private static readonly Regex SpotifyRe = new(@"^https?://open\.spotify\.com/(intl-[\w-]+/)?track/", RegexOptions.IgnoreCase);
    private static readonly Regex UrlRe = new(@"^https?://", RegexOptions.IgnoreCase);

    private readonly Hub _hub;
    public readonly List<Track> Queue = new();
    public Track Current;
    public readonly HashSet<string> Votes = new();
    public readonly Player Player = new();
    public readonly Chime Chime = new();
    private TaskCompletionSource<bool> _wake = new();
    public int ToastId;
    public bool Loading;
    // Загрузки по ID трека: очередь скачивается заранее, чтобы следующий трек начинался сразу
    private readonly Dictionary<string, Task<string>> _downloads = new();
    public string ToastText = "";

    /// <summary>Замена поиска в тестах.</summary>
    public Func<string, Task<Track>> ResolveOverride;

    // ---------- выравнивание громкости ----------
    // Измерения по ID трека: громкость (LUFS) и пик. Хранятся в кэше, чтобы не мерить трек дважды.
    private readonly ConcurrentDictionary<string, double[]> _loud = new();
    private static string LoudFile => Path.Combine(Config.CacheDir, "loudness.json");
    private bool _loudDirty;

    // ---------- воспроизведение в OBS ----------
    // OBS-страница /music-obs опрашивает программу дважды в секунду и сама играет текущий файл.
    private DateTime _obsSeen = DateTime.MinValue;
    private bool _obsActive;          // текущий трек звучит в OBS, а не в программе
    private int _obsGen;
    private string _obsPath = "";
    private double _obsPos, _obsStart;
    private bool _obsPaused;
    private bool _obsWarned;
    private readonly HashSet<string> _obsFailed = new();   // треки, которые OBS не смог открыть

    public bool ObsMode => Cfg.Str("output", "app") == "obs";
    public bool ObsConnected => (DateTime.UtcNow - _obsSeen).TotalSeconds < 4;
    /// <summary>Путь к файлу, который сейчас играет в OBS (для отдачи по /api/music/audio).</summary>
    public string ObsFile(int gen) => _obsActive && gen == _obsGen ? _obsPath : null;

    private double Position => _obsActive ? _obsPos : Player.Position;
    private bool IsPaused => _obsActive ? _obsPaused : Player.Paused;

    public Music(Hub hub)
    {
        _hub = hub;
        Liked = new JsonStore(Path.Combine(Config.Profile, "liked.json"), new JsonObject { ["items"] = new JsonArray() });
    }

    // ---------- «лайки»: понравившиеся треки попадают в резервный плейлист ----------
    public readonly JsonStore Liked;
    public JsonArray LikedItems => (JsonArray)Liked.Data["items"];
    public bool IsLiked(string vid) => vid != "" && LikedItems.Any(x => x.Str("vid") == vid);
    private bool LikedInFallback => Cfg.Bool("fallback_liked", true) && LikedItems.Count > 0;

    /// <summary>Лайк/снятие лайка текущего трека (горячая клавиша, панель, док-панель).</summary>
    public (bool Liked, string Message) ToggleLikeCurrent()
    {
        var t = Current;
        if (t == null) throw new UserError("Сейчас ничего не играет");
        return ToggleLike(t.Vid, t.Title, t.PageUrl, t.Source, t.Duration, t.Requester);
    }

    public (bool Liked, string Message) ToggleLike(string vid, string title, string url, string source, int duration, string requester)
    {
        if (string.IsNullOrEmpty(vid)) throw new UserError("У этого трека нет ссылки — его не получится сохранить");
        var arr = LikedItems;
        for (var i = arr.Count - 1; i >= 0; i--)
            if (arr[i].Str("vid") == vid)
            {
                arr.RemoveAt(i);
                Liked.Save();
                _fbOrder.Remove(vid);
                _hub.Log.Add("music", $"♡ Убран из понравившихся: {title}");
                return (false, $"«{title}» убран из резервного плейлиста");
            }
        if (arr.Count >= 2000) throw new UserError("В понравившихся уже 2000 треков — удалите лишние");
        arr.Add(new JsonObject
        {
            ["vid"] = vid, ["title"] = title, ["url"] = url ?? "", ["source"] = source ?? "youtube",
            ["duration"] = duration, ["requester"] = requester ?? "", ["ts"] = ActionLog.Now,
        });
        Liked.Save();
        _fbBad.Remove(vid);
        _hub.Log.Add("music", $"♥ Понравился: {title} — добавлен в резервный плейлист");
        if (Current == null && Queue.Count == 0) Wake();   // тишина — резервный плейлист может начать играть
        var msg = $"«{title}» добавлен в резервный плейлист";
        if (!Cfg.Bool("fallback_enabled")) msg += " (сам плейлист сейчас выключен — включите его в «Музыка → Плеер»)";
        else if (!Cfg.Bool("fallback_liked", true)) msg += " (понравившиеся сейчас не играют — включите это в настройках резервного плейлиста)";
        return (true, msg);
    }

    public void Unlike(string vid)
    {
        var arr = LikedItems;
        for (var i = arr.Count - 1; i >= 0; i--) if (arr[i].Str("vid") == vid) arr.RemoveAt(i);
        Liked.Save();
        _fbOrder.Remove(vid);
    }

    private JsonObject Cfg => _hub.S.Obj("music");

    public void ApplySettings()
    {
        var v = Math.Max(0, Math.Min(100, Cfg.Int("volume", 60))) / 100.0;
        Player.Volume = (float)(v * v);
        Player.DeviceId = Cfg.Str("audio_device");
        if (Current != null) Player.GainDb = GainDb(Current.Vid);
        // режим переключили на «программа», а трек играет в OBS — продолжаем в программе с того же места
        if (_obsActive && !ObsMode) MoveToApp("режим «музыка в программе»");
        FallbackChanged();
    }

    /// <summary>Поправка громкости трека в дБ: до целевого уровня, не больше +12 / −12 дБ.</summary>
    public double GainDb(string vid)
    {
        if (!Cfg.Bool("normalize", true) || !_loud.TryGetValue(vid, out var m)) return 0;
        var target = Cfg.Num("normalize_target", -14);
        var g = Math.Clamp(target - m[0], -12, 12);
        // не задираем трек так, что больше 6 дБ уйдёт в лимитер
        if (m[1] > 0) g = Math.Min(g, -20 * Math.Log10(m[1]) + 6);
        return Math.Round(g, 1);
    }

    private void LoadLoudness()
    {
        try
        {
            if (!File.Exists(LoudFile)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, double[]>>(File.ReadAllText(LoudFile));
            if (d != null) foreach (var (k, v) in d) if (v?.Length == 2) _loud[k] = v;
        }
        catch { }
    }

    private void SaveLoudness()
    {
        if (!_loudDirty) return;
        _loudDirty = false;
        try
        {
            // храним только последние ~2000 треков
            var d = _loud.Take(2000).ToDictionary(p => p.Key, p => p.Value);
            File.WriteAllText(LoudFile, JsonSerializer.Serialize(d));
        }
        catch { }
    }

    /// <summary>Измеряет громкость скачанного файла (в фоне, ~1 с на трек).</summary>
    private async Task Measure(string vid, string path)
    {
        if (!Cfg.Bool("normalize", true) || _loud.ContainsKey(vid)) return;
        try
        {
            var m = await Task.Run(() => Loudness.Measure(path));
            if (m == null) return;
            _loud[vid] = new[] { m.Value.Lufs, m.Value.Peak };
            _loudDirty = true;
            SaveLoudness();
        }
        catch (Exception e) { _hub.Log.Add("skip", $"Не удалось измерить громкость: {e.Message}"); }
    }

    public void Wake() => _wake.TrySetResult(true);

    public void ShowToast(string text)
    {
        ToastId++;
        ToastText = text;
    }

    public async Task Run()
    {
        ApplySettings();
        Directory.CreateDirectory(Config.CacheDir);
        CleanupCache();
        LoadLoudness();
        RestoreQueue();
        _ = ObsWatch();
        _ = PersistLoop();
        _ = CrossfadeWatch();
        while (true)
        {
            await _wake.Task;
            _wake = new TaskCompletionSource<bool>();
            if (Current == null && Queue.Count > 0) await PlayNext();
            else if (Current == null && Queue.Count == 0 && FallbackOn) await PlayFallback();
        }
    }

    // ---------- сохранение очереди (на случай вылета или перезапуска) ----------
    private static string QueueFile => Path.Combine(Config.Profile, "queue.json");
    private string _savedSig = "";
    private double _savedPos = -1;

    private static JsonObject TrackJson(Track t, double pos = 0) => new()
    {
        ["vid"] = t.Vid, ["title"] = t.Title, ["url"] = t.PageUrl, ["duration"] = t.Duration, ["requester"] = t.Requester,
        ["login"] = t.Login, ["priority"] = t.Priority, ["source"] = t.Source, ["views"] = t.Views,
        ["reward_id"] = t.Redemption?.RewardId ?? "", ["redemption_id"] = t.Redemption?.RedemptionId ?? "",
        ["cost"] = t.Redemption?.Cost ?? 0, ["position"] = Math.Round(pos, 1),
    };

    private static Track TrackFrom(JsonNode n) => new()
    {
        Vid = n.Str("vid"), Title = n.Str("title"), PageUrl = n.Str("url"), Duration = n.Int("duration"),
        Requester = n.Str("requester"), Login = n.Str("login"), Priority = n.Int("priority"), Source = n.Str("source", "youtube"),
        Views = (long)n.Num("views", -1), ResumeAt = n.Num("position"),
        Redemption = n.Str("redemption_id") != "" ? (n.Str("reward_id"), n.Str("redemption_id"), n.Int("cost")) : null,
    };

    /// <summary>Записывает очередь и текущий трек. Заказы резервного плейлиста не сохраняются — они и так вернутся.</summary>
    public void SaveQueue(bool force = false)
    {
        try
        {
            var cur = Current != null && !Current.Fallback ? Current : null;
            var queue = Queue.Where(t => !t.Fallback).ToList();
            var sig = (cur?.Id ?? "") + "|" + string.Join(",", queue.Select(t => t.Id + ":" + t.Priority));
            var pos = cur == null ? 0 : Loading ? cur.ResumeAt : Position;   // пока трек грузится, плеер ещё не на нём
            // очередь не менялась — позицию текущего трека обновляем раз в 10 секунд
            if (!force && sig == _savedSig && (cur == null || Math.Abs(pos - _savedPos) < 10)) return;
            var js = new JsonObject
            {
                ["saved"] = ActionLog.Now,
                ["current"] = cur != null ? TrackJson(cur, pos) : null,
                ["queue"] = new JsonArray(queue.Select(t => (JsonNode)TrackJson(t)).ToArray()),
            };
            var tmp = QueueFile + ".tmp";
            File.WriteAllText(tmp, js.ToJsonString());
            File.Move(tmp, QueueFile, true);
            _savedSig = sig; _savedPos = pos;
        }
        catch (Exception e) { _hub.Log.Add("skip", "Сохранение очереди: " + e.Message); }
    }

    private async Task PersistLoop()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            SaveQueue();
        }
    }

    /// <summary>При запуске: возвращает очередь и недоигранный трек (если сохранены не больше N часов назад).</summary>
    private void RestoreQueue()
    {
        try
        {
            if (!File.Exists(QueueFile)) return;
            var js = JsonNode.Parse(File.ReadAllText(QueueFile));
            if (!Cfg.Bool("restore_queue", true)) return;
            var age = ActionLog.Now - js.Num("saved");
            var maxH = Math.Clamp(Cfg.Int("restore_hours", 12), 1, 168);
            if (age > maxH * 3600)
            {
                if ((js["queue"] as JsonArray)?.Count > 0 || js["current"] != null)
                    _hub.Log.Add("music", $"Старая очередь (сохранена {(int)(age / 3600)} ч назад) не восстановлена — больше {maxH} ч");
                return;
            }
            var restored = new List<Track>();
            if (js["current"] is JsonObject c && c.Str("vid") != "") restored.Add(TrackFrom(c));
            foreach (var n in js["queue"] as JsonArray ?? new JsonArray())
                if (n.Str("vid") != "") { var t = TrackFrom(n); t.ResumeAt = 0; restored.Add(t); }
            if (restored.Count == 0) return;
            Queue.InsertRange(0, restored);
            var first = restored[0];
            var resume = first.ResumeAt > 3 ? $", «{first.Title}» продолжится с {(int)first.ResumeAt / 60}:{(int)first.ResumeAt % 60:00}" : "";
            _hub.Log.Add("music", $"Очередь восстановлена после перезапуска: {restored.Count} {(restored.Count == 1 ? "трек" : restored.Count < 5 ? "трека" : "треков")}{resume}");
            RestoredInfo = $"Очередь восстановлена: {restored.Count} тр.";
            RestoredAt = ActionLog.Now;
            Wake();
        }
        catch (Exception e) { _hub.Log.Add("skip", "Восстановление очереди: " + e.Message); }
    }

    public string RestoredInfo = "";
    public double RestoredAt;

    // ---------- резервный плейлист ----------
    private List<Track> _fbList = new();
    private string _fbUrl = "";
    private DateTime _fbLoaded = DateTime.MinValue, _fbPauseUntil = DateTime.MinValue;
    private readonly List<string> _fbOrder = new();
    private readonly HashSet<string> _fbBad = new();
    private int _fbFails;
    public string FallbackStatus { get; private set; } = "выключен";

    private bool FallbackOn => Cfg.Bool("fallback_enabled") && (Cfg.Str("fallback_url").Trim() != "" || LikedInFallback);

    /// <summary>Загружает список треков плейлиста (YouTube) — раз в 6 часов или при смене ссылки.</summary>
    public async Task<int> LoadFallback(bool force = false)
    {
        var url = Cfg.Str("fallback_url").Trim();
        if (url == "")
        {
            // только понравившиеся треки, без плейлиста YouTube
            _fbList = new(); _fbUrl = "";
            FallbackStatus = LikedInFallback ? $"{LikedItems.Count} понравившихся" : "ссылка не указана";
            return LikedInFallback ? LikedItems.Count : 0;
        }
        if (!force && url == _fbUrl && _fbList.Count > 0 && DateTime.UtcNow - _fbLoaded < TimeSpan.FromHours(6)) return _fbList.Count;
        FallbackStatus = "загружаю плейлист…";
        try
        {
            var list = await _hub.YtDlp.Playlist(url);
            var maxSec = Cfg.Int("max_duration_min", 8) * 60;
            _fbList = list.Where(t => t.Duration <= 0 || t.Duration <= Math.Max(maxSec, 900)).ToList();
            _fbUrl = url;
            _fbLoaded = DateTime.UtcNow;
            _fbOrder.Clear();
            FallbackStatus = (_fbList.Count > 0 ? $"{_fbList.Count} треков" : "в плейлисте нет треков")
                + (LikedInFallback ? $" + {LikedItems.Count} понравившихся" : "");
            _hub.Log.Add("music", $"Резервный плейлист: {FallbackStatus}");
            return _fbList.Count;
        }
        catch (Exception e)
        {
            FallbackStatus = "не удалось загрузить: " + Reason(e);
            _hub.Log.Error("Резервный плейлист: " + FallbackStatus);
            throw;
        }
    }

    private Track NextFallback()
    {
        // общий пул: плейлист YouTube + понравившиеся треки (без повторов)
        var pool = new Dictionary<string, Track>();
        foreach (var t in _fbList) pool.TryAdd(t.Vid, t);
        if (LikedInFallback)
            foreach (var x in LikedItems)
                pool.TryAdd(x.Str("vid"), new Track
                {
                    Vid = x.Str("vid"), Title = x.Str("title"), PageUrl = x.Str("url"), Duration = x.Int("duration"),
                    Source = x.Str("source", "youtube"),
                });
        _fbOrder.RemoveAll(v => !pool.ContainsKey(v));
        if (_fbOrder.Count == 0)
        {
            var ids = pool.Keys.Where(v => !_fbBad.Contains(v)).ToList();
            if (Cfg.Bool("fallback_shuffle", true)) ids = ids.OrderBy(_ => Random.Shared.Next()).ToList();
            _fbOrder.AddRange(ids);
        }
        if (_fbOrder.Count == 0) return null;
        var vid = _fbOrder[0];
        _fbOrder.RemoveAt(0);
        var src = pool[vid];
        return new Track
        {
            Vid = src.Vid, Title = src.Title, PageUrl = src.PageUrl, Duration = src.Duration, Source = src.Source,
            Requester = "", Login = "", Fallback = true,
        };
    }

    private async Task PlayFallback()
    {
        if (DateTime.UtcNow < _fbPauseUntil) { _ = WakeLater(_fbPauseUntil - DateTime.UtcNow); return; }
        try { await LoadFallback(); }
        catch { _fbPauseUntil = DateTime.UtcNow.AddMinutes(2); _ = WakeLater(TimeSpan.FromMinutes(2)); return; }
        if (Queue.Count > 0 || Current != null) { Wake(); return; }   // пока грузили — пришёл заказ
        var t = NextFallback();
        if (t == null) return;
        Queue.Insert(0, t);
        await PlayNext();
        if (Current == null && !Queue.Any())   // не скачался
        {
            _fbBad.Add(t.Vid);
            if (++_fbFails >= 3)
            {
                _fbFails = 0;
                _fbPauseUntil = DateTime.UtcNow.AddMinutes(2);
                _hub.Log.Error("Резервный плейлист: три трека подряд не загрузились — пауза 2 минуты (проверьте связь с YouTube)");
            }
        }
        else _fbFails = 0;
    }

    private async Task WakeLater(TimeSpan delay)
    {
        await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        Wake();
    }

    /// <summary>Включили/выключили резервный плейлист или сменили ссылку.</summary>
    public void FallbackChanged()
    {
        if (!FallbackOn)
        {
            FallbackStatus = Cfg.Bool("fallback_enabled") ? "нет ссылки и понравившихся треков" : "выключен";
            if (Current?.Fallback == true) Skip("резервный плейлист выключен");
            return;
        }
        if (Cfg.Str("fallback_url").Trim() != _fbUrl) { _fbList = new(); _fbOrder.Clear(); _fbBad.Clear(); }
        _fbPauseUntil = DateTime.MinValue;
        Wake();
    }

    // ---------- команды зрителей ----------
    private int SecondsUntil(int queueIndex)
    {
        var s = 0;
        if (Current != null && !Current.Fallback) s += Math.Max(0, Current.Duration - (int)Position);
        for (var i = 0; i < queueIndex && i < Queue.Count; i++) s += Math.Max(0, Queue[i].Duration);
        return s;
    }

    private static string Mins(int sec) => sec < 60 ? "меньше минуты" : $"~{(sec + 30) / 60} мин";

    public string TrackText()
    {
        var cur = Current;
        if (cur == null) return "Сейчас ничего не играет";
        var who = cur.Fallback ? " (резервный плейлист)" : cur.Requester != "" ? $" (заказ: {cur.Requester})" : "";
        var link = cur.PageUrl != "" && !cur.Vid.StartsWith("ym:") ? " " + cur.PageUrl : "";
        return $"Сейчас играет: {cur.Title}{who}{link}";
    }

    public string QueueText()
    {
        var q = Queue.Where(t => !t.Fallback).ToList();
        if (q.Count == 0) return "Очередь пуста — закажите трек за баллы канала";
        var parts = q.Take(5).Select((t, i) => $"{i + 1}. {Short(t.Title, 40)}" + (t.Requester != "" ? $" ({t.Requester})" : ""));
        var more = q.Count > 5 ? $" и ещё {q.Count - 5}" : "";
        return $"В очереди {q.Count}: " + string.Join(" · ", parts) + more + $". Вся очередь ≈ {Mins(SecondsUntil(Queue.Count))}";
    }

    public string MyTrack(string login, string name)
    {
        var idx = Queue.FindIndex(t => t.Login == login);
        if (Current != null && Current.Login == login && idx < 0) return $"{name}, ваш трек «{Short(Current.Title, 50)}» играет сейчас";
        if (idx < 0) return $"{name}, ваших треков в очереди нет";
        var t = Queue[idx];
        var n = Queue.Count(x => x.Login == login);
        var extra = n > 1 ? $" (всего ваших: {n})" : "";
        return $"{name}, «{Short(t.Title, 50)}» — {idx + 1}-й в очереди, заиграет через {Mins(SecondsUntil(idx))}{extra}";
    }

    public string CancelOwn(string login, string name)
    {
        var idx = Queue.FindLastIndex(t => t.Login == login);
        if (idx < 0) return $"{name}, в очереди нет ваших треков (играющий трек отменить нельзя)";
        var t = Queue[idx];
        Queue.RemoveAt(idx);
        _hub.Log.Add("music", $"{login} отменил свой заказ «{t.Title}»");
        var paid = t.Redemption != null ? " Баллы за заказ не возвращаются." : "";
        return $"{name}, «{Short(t.Title, 50)}» убран из очереди.{paid}";
    }

    private static void CleanupCache()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Config.CacheDir).GetFiles())
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) f.Delete();
        }
        catch { }
    }

    private async Task<string> DownloadAudio(Track t)
    {
        var p = await DownloadRaw(t);
        await Measure(t.Vid, p);
        return p;
    }

    private async Task<string> DownloadRaw(Track t)
    {
        Directory.CreateDirectory(Config.CacheDir);
        if (t.Vid.StartsWith("ym:"))
        {
            var path = Path.Combine(Config.CacheDir, "ym_" + t.Vid[3..] + ".mp3");
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            var url = await _hub.Yandex.DirectLink(t.Vid[3..]);
            var bytes = await _hub.Http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        return await _hub.YtDlp.DownloadAudio(t.Vid, Config.CacheDir);
    }

    private async Task PlayNext()
    {
        var t = Queue[0];
        Queue.RemoveAt(0);
        Current = t;
        Votes.Clear();
        string path;
        Loading = true;
        try
        {
            if (!t.Vid.StartsWith("ym:") && _hub.YtDlp.Installing)
                _hub.Log.Add("music", "Готовлю плеер к первому запуску (загрузка yt-dlp)…");
            path = await GetAudio(t);
        }
        catch (Exception e)
        {
            _downloads.Remove(t.Vid);
            var reason = Reason(e);
            // YouTube не отдал трек — пробуем найти этот же трек в Яндекс Музыке
            path = Current == t ? await YandexInstead(t, reason) : null;
            if (path == null)
            {
                Loading = false;
                _hub.Log.Error($"Не удалось загрузить «{t.Title}»: {reason}");
                if (Current == t) Current = null;
                Wake();
                await TellViewer(t, reason);
                return;
            }
        }
        finally { Loading = false; }
        if (Current != t) return;   // пропущен во время загрузки
        try
        {
            var startAt = t.ResumeAt > 3 && (t.Duration <= 0 || t.ResumeAt < t.Duration - 5) ? t.ResumeAt - 2 : 0;
            t.ResumeAt = 0;
            StartPlayback(t, path, startAt);
            try
            {
                var url = t.PageUrl != "" ? t.PageUrl : t.Vid.StartsWith("ym:") ? "" : $"https://www.youtube.com/watch?v={t.Vid}";
                _hub.Stats.Played(_hub.Stream.Key, t.Vid, t.Title, url, t.Source, t.Requester, t.Login, t.Duration, t.Fallback);
            }
            catch (Exception e) { _hub.Log.Add("skip", "История треков: " + e.Message); }
        }
        catch (Exception e)
        {
            _hub.Log.Error($"Плеер не смог открыть «{t.Title}»: файл повреждён или формат не поддерживается ({e.Message})");
            Current = null;
            Wake();
            await TellViewer(t, "не удалось открыть звук этого трека");
            return;
        }
        _hub.Log.Add("music", $"Играет: {t.Title}" + (t.Requester != "" ? $" (заказ {t.Requester})" : ""));
        ShowToast($"♪ {t.Title}");
        Prefetch();
    }

    // ---------- запасной источник: Яндекс Музыка ----------
    private static readonly Regex TitleJunk = new(
        @"\s*[\(\[](?:official|officiall?y|video|audio|lyrics?|lyric video|music video|mv|hd|hq|4k|премьера|клип|official music video|visuali[sz]er|текст|караоке|remaster(?:ed)?[^\)\]]*|\d{4})[^\)\]]*[\)\]]|\s*\|.*$|\s*(?:official\s+(?:music\s+)?video|lyrics?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Чистое «исполнитель — название» для поиска: без «(Official Video)», «[HD]», «| Премьера» и т. п.</summary>
    public static string CleanTitle(string title)
    {
        var s = TitleJunk.Replace(title ?? "", "");
        s = Regex.Replace(s, @"\s+(?:ft\.?|feat\.?)\s+.*$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '-', '–', '—');
    }

    /// <summary>Скачивает тот же трек из Яндекс Музыки. null — не нашёлся или запасной источник выключен.</summary>
    private async Task<string> YandexInstead(Track t, string reason)
    {
        if (t.Vid.StartsWith("ym:") || !Cfg.Bool("yandex_fallback", true) || _hub.Yandex.Token == "") return null;
        var query = CleanTitle(t.Title);
        if (query.Length < 2) return null;
        try
        {
            var y = await _hub.Yandex.Search(query);
            // длительность должна быть похожей — иначе это скорее другой трек (ремикс, кавер)
            if (t.Duration > 0 && y.Duration > 0 && Math.Abs(t.Duration - y.Duration) > Math.Max(25, t.Duration * 0.15))
            {
                _hub.Log.Add("skip", $"Яндекс Музыка: «{y.Title}» не похож по длительности на «{t.Title}» — не подменяю");
                return null;
            }
            if (Current != t) return null;
            var oldTitle = t.Title;
            t.Vid = "ym:" + y.Id; t.Title = y.Title; t.PageUrl = y.Url; t.Source = "yandex";
            if (y.Duration > 0) t.Duration = y.Duration;
            Loading = true;
            var path = await GetAudio(t);
            _hub.Log.Add("music", $"YouTube не отдал «{oldTitle}» ({reason}) — играет из Яндекс Музыки: {y.Title}");
            return path;
        }
        catch (Exception e)
        {
            _hub.Log.Add("skip", $"Яндекс Музыка как запасной источник: {e.Message}");
            return null;
        }
    }

    /// <summary>Понятная причина ошибки на русском.</summary>
    public static string Reason(Exception e) => e is ResolveException ? e.Message : YtDlp.Explain(e.Message);

    /// <summary>Сообщает зрителю в чат, что его трек не удалось включить.</summary>
    private async Task TellViewer(Track t, string reason)
    {
        if (t.Login == "" || !Cfg.Bool("chat_errors", true)) return;
        var title = t.Title.Length > 80 ? t.Title[..80] + "…" : t.Title;
        try { await _hub.Twitch.SendChat($"@{t.Requester}, не удалось включить «{title}»: {reason}. Трек пропущен"); }
        catch (Exception e) { _hub.Log.Add("skip", $"Не удалось написать в чат: {e.Message}"); }
    }

    /// <summary>Мягкий сигнал о новом заказе (настройки «Музыка → Звук при заказе»).</summary>
    public void PlayChime(bool force = false)
    {
        if (!force && !Cfg.Bool("order_sound", true)) return;
        try { Chime.Play(Cfg.Str("order_sound_style", "bell"), Cfg.Int("order_sound_volume", 50), Cfg.Str("audio_device"), force); }
        catch (Exception e) { _hub.Log.Add("skip", $"Звук заказа: {e.Message}"); }
    }

    private Task<string> GetAudio(Track t)
    {
        if (_downloads.TryGetValue(t.Vid, out var task) && !task.IsFaulted && !task.IsCanceled) return task;
        task = DownloadAudio(t);
        _downloads[t.Vid] = task;
        return task;
    }

    /// <summary>Заранее скачивает два следующих трека очереди.</summary>
    private void Prefetch()
    {
        foreach (var t in Queue.Take(2).ToList())
        {
            if (_downloads.ContainsKey(t.Vid)) continue;
            var task = GetAudio(t);
            var vid = t.Vid;
            _ = task.ContinueWith(x =>
            {
                _hub.Log.Add("skip", $"Предзагрузка «{t.Title}» не удалась: {x.Exception?.GetBaseException().Message}");
                _hub.Loop.Post(() => _downloads.Remove(vid));
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        // не держим в памяти задачи давно сыгранных треков
        if (_downloads.Count > 30)
        {
            var keep = new HashSet<string>(Queue.Select(q => q.Vid));
            if (Current != null) keep.Add(Current.Vid);
            foreach (var k in _downloads.Keys.Where(k => !keep.Contains(k)).ToList()) _downloads.Remove(k);
        }
    }

    /// <summary>Запуск трека: в OBS (если выбран этот режим и OBS-страница на связи) или в программе.</summary>
    private void StartPlayback(Track t, string path, double startAt)
    {
        var fadeIn = _nextFade;
        _nextFade = 0;
        var gain = GainDb(t.Vid);
        if (gain != 0) _hub.Log.Add("skip", $"Громкость «{t.Title}» выровнена на {gain:+0.0;-0.0} дБ");
        if (ObsMode && ObsConnected && !_obsFailed.Contains(t.Vid))
        {
            Player.Stop();
            _obsGen = Player.Gen;
            _obsActive = true;
            _obsPath = path;
            _obsPos = _obsStart = startAt;
            _obsPaused = false;
            _obsFade = fadeIn;
            return;
        }
        if (ObsMode && !ObsConnected && !_obsWarned)
        {
            _obsWarned = true;
            _hub.Log.Error("Музыка: выбран режим «в OBS», но источник с плеером в OBS не открыт — играю из программы. Добавьте в OBS источник «Браузер» с адресом из раздела «Музыка».");
        }
        _obsActive = false;
        Player.Play(path, OnEnd, gain, startAt, fadeIn);
    }

    /// <summary>Переносит текущий трек из OBS в программу с того же места.</summary>
    private void MoveToApp(string why)
    {
        if (!_obsActive || Current == null) { _obsActive = false; return; }
        var t = Current;
        var paused = _obsPaused;
        _obsActive = false;
        _hub.Log.Add("music", $"Продолжаю «{t.Title}» в программе ({why})");
        try
        {
            Player.Play(_obsPath, OnEnd, GainDb(t.Vid), _obsPos);
            if (paused) Player.SetPaused(true);
        }
        catch (Exception e)
        {
            _hub.Log.Error($"Плеер не смог открыть «{t.Title}»: {e.Message}");
            Current = null;
            Wake();
        }
    }

    /// <summary>Если OBS закрыли посреди трека — через 4 с музыка продолжится в программе.</summary>
    private async Task ObsWatch()
    {
        while (true)
        {
            await Task.Delay(1000);
            if (_obsActive && !ObsConnected) MoveToApp("OBS-плеер не отвечает");
            SaveLoudness();
        }
    }

    // ---------- плавный переход между треками (кроссфейд) ----------
    private double _nextFade, _obsFade;
    private int _xfadeGen = -1;
    private string _prepicked = "";
    public int CrossfadeSec => Math.Clamp(Cfg.Int("crossfade", 4), 0, 12);

    /// <summary>
    /// За N секунд до конца трека запускает следующий поверх затухающего. Следующий трек должен быть уже скачан,
    /// иначе переход обычный. Резервный трек выбирается заранее (уступит место, если придёт заказ).
    /// </summary>
    private async Task CrossfadeWatch()
    {
        while (true)
        {
            await Task.Delay(250);
            try
            {
                var cf = CrossfadeSec;
                var cur = Current;
                if (cf <= 0 || cur == null || Loading || IsPaused) continue;
                var gen = _obsActive ? _obsGen : Player.Gen;
                if (gen == _xfadeGen) continue;
                var total = _obsActive ? cur.Duration : Player.Total;
                if (total < cf * 3) continue;   // очень короткий трек — без перехода
                var left = total - Position;
                // резервный трек выбираем заранее, чтобы успел скачаться
                if (Queue.Count == 0 && FallbackOn && left <= cf + 15 && _prepicked != cur.Id)
                {
                    _prepicked = cur.Id;
                    try { await LoadFallback(); } catch { }
                    var fb = Queue.Count == 0 && Current == cur ? NextFallback() : null;
                    if (fb != null) { Queue.Add(fb); Prefetch(); }
                }
                if (left > cf || left <= 0.3 || Queue.Count == 0) continue;
                var next = Queue[0];
                if (!_downloads.TryGetValue(next.Vid, out var task) || !task.IsCompletedSuccessfully) continue;
                _xfadeGen = gen;
                _nextFade = cf;
                if (_obsActive) { _obsActive = false; OnEnd(_obsGen, ""); }
                else
                {
                    Player.FadeOut(cf);
                    Current = null;
                    Wake();
                }
            }
            catch (Exception e) { _hub.Log.Add("skip", "Плавный переход: " + e.Message); }
        }
    }

    /// <summary>Опрос от OBS-страницы: она сообщает позицию/окончание/ошибку и получает, что играть.</summary>
    public object ObsPoll(int gen, double pos, int ended, string err)
    {
        var wasConnected = ObsConnected;
        _obsSeen = DateTime.UtcNow;
        if (!wasConnected) { _obsWarned = false; _hub.Log.Add("music", "OBS-плеер подключён"); }
        if (_obsActive && gen == _obsGen)
        {
            if (pos > 0) _obsPos = pos;
            if (!string.IsNullOrEmpty(err))
            {
                _hub.Log.Add("skip", $"OBS-плеер: {err}");
                if (Current != null) _obsFailed.Add(Current.Vid);
                MoveToApp("OBS не смог открыть этот файл");
            }
            else if (ended == gen)
            {
                _obsActive = false;
                OnEnd(gen, "");
            }
        }
        var active = _obsActive;
        var gainLin = Player.Volume * Player.Duck * Math.Pow(10, (Current != null ? GainDb(Current.Vid) : 0) / 20);
        return new
        {
            gen = active ? _obsGen : 0,
            src = active ? $"/api/music/audio/{_obsGen}{Path.GetExtension(_obsPath)}" : "",
            start = _obsStart,
            paused = active && _obsPaused,
            gain = Math.Round(gainLin, 4),
            fade = Math.Round(_obsFade, 2),
        };
    }

    private void OnEnd(int gen, string error)
    {
        if (gen != Player.Gen) return;
        if (error != "") _hub.Log.Error($"Плеер: воспроизведение прервалось ({error})");
        Current = null;
        Wake();
    }

    // ---------- управление ----------
    public bool Skip(string reason = "")
    {
        if (Current == null) return false;
        var title = Current.Title;
        if (CrossfadeSec > 0 && !_obsActive) Player.FadeOut(0.6); else Player.Stop();   // пропуск — короткое затухание без щелчка
        _nextFade = CrossfadeSec > 0 ? 0.6 : 0;
        _obsActive = false;
        Current = null;
        _hub.Log.Add("music", $"Пропущен: {title}" + (reason != "" ? $" ({reason})" : ""));
        Wake();
        return true;
    }

    public bool TogglePause()
    {
        if (_obsActive) { _obsPaused = !_obsPaused; return _obsPaused; }
        Player.SetPaused(!Player.Paused);
        return Player.Paused;
    }

    public void SetVolume(int v)
    {
        Cfg["volume"] = Math.Max(0, Math.Min(100, v));
        _hub.Settings.Save();
        ApplySettings();
    }

    public Track Remove(string id)
    {
        var t = Queue.FirstOrDefault(x => x.Id == id);
        if (t != null) Queue.Remove(t);
        return t;
    }

    public void Move(string id, int delta)
    {
        var i = Queue.FindIndex(x => x.Id == id);
        if (i < 0) return;
        var t = Queue[i];
        Queue.RemoveAt(i);
        Queue.Insert(Math.Max(0, Math.Min(Queue.Count, i + delta)), t);
    }

    public string VoteSkip(ChatMsg msg)
    {
        var cur = Current;
        if (cur == null) return "Сейчас ничего не играет";
        if (!Cfg.Bool("skip_vote_enabled", true) && !msg.IsMod) return "Голосование за пропуск выключено";
        if (msg.IsMod || (cur.Login != "" && msg.Login == cur.Login))
        {
            Skip($"!skip от {msg.Login}");
            _hub.Stats.Music(_hub.Stream.Key, "vote_skip", "ok", msg.Login, msg.Name, cur.Title);
            return $"Трек «{cur.Title}» пропущен";
        }
        var need = Math.Max(1, Cfg.Int("skip_votes", 3));
        if (Votes.Contains(msg.Login)) return $"{msg.Name}, вы уже голосовали ({Votes.Count}/{need})";
        Votes.Add(msg.Login);
        if (Votes.Count >= need)
        {
            Skip("голосование");
            _hub.Stats.Music(_hub.Stream.Key, "vote_skip", "ok", msg.Login, msg.Name, cur.Title);
            return $"Голосование: трек «{cur.Title}» пропущен";
        }
        return $"Голос за пропуск принят ({Votes.Count}/{need})";
    }

    // ---------- поиск ----------
    private async Task<string> SpotifyQuery(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        using var resp = await _hub.Http.SendAsync(req);
        var page = await resp.Content.ReadAsStringAsync();
        var m = Regex.Match(page, "<title>(.*?)</title>", RegexOptions.Singleline);
        var title = m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        title = Regex.Replace(title, @"\s*\|\s*Spotify\s*$", "");
        title = Regex.Replace(title, @"\s+-\s+song( and lyrics)? by\s+", " ", RegexOptions.IgnoreCase);
        if (title.Trim() == "") throw new ResolveException("не удалось прочитать трек Spotify");
        return title.Trim();
    }

    private List<string> SearchOrder()
    {
        var src = Cfg.Obj("sources");
        var order = Cfg.Str("search_first") == "yandex" ? new[] { "yandex", "youtube" } : new[] { "youtube", "yandex" };
        return order.Where(s => (s == "youtube" && src.Bool("search")) ||
                                (s == "yandex" && src.Bool("yandex") && _hub.Yandex.Token != "")).ToList();
    }

    private static Track FromYandex(YandexTrack y, string source)
    {
        if (!y.Available) throw new ResolveException("трек недоступен в Яндекс Музыке");
        return new Track { Vid = "ym:" + y.Id, Title = y.Title, PageUrl = y.Url, Duration = y.Duration, Source = source };
    }

    private Task<Track> YtByUrl(string url, string source) => _hub.YtDlp.ByUrl(url, source);

    private Task<Track> YtSearch(string q, string source, long minViews) => _hub.YtDlp.Search(q, source, minViews);

    private static string Short(string s) => s.Length > 120 ? s[..120] : s;
    private static string Short(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;

    private async Task<Track> SearchAny(string q, string source, long minViews)
    {
        var order = SearchOrder();
        if (order.Count == 0) throw new ResolveException("поиск по названию отключён, пришлите ссылку");
        var errors = new List<string>();
        foreach (var s in order)
        {
            try
            {
                return s == "yandex" ? FromYandex(await _hub.Yandex.Search(q), source) : await YtSearch(q, source, minViews);
            }
            catch (Exception e)   // пробуем следующий источник
            {
                var why = e switch
                {
                    YandexException or ResolveException => e.Message,
                    HttpRequestException or TaskCanceledException when s == "yandex" => "нет связи с Яндекс Музыкой",
                    _ when s == "yandex" => "ошибка ответа Яндекс Музыки",
                    _ => YtDlp.Explain(e.Message),
                };
                if (e is not (YandexException or ResolveException)) _hub.Log.Add("skip", $"Поиск ({s}): {Short(e.Message)}");
                errors.Add(order.Count > 1 ? (s == "yandex" ? "Яндекс Музыка: " : "YouTube: ") + why : why);
            }
        }
        throw new ResolveException(string.Join("; ", errors));
    }

    public async Task<Track> Resolve(string query, long minViews = 0)
    {
        if (ResolveOverride != null) return await ResolveOverride(query);
        var q = query.Trim();
        var src = Cfg.Obj("sources");
        if (SpotifyRe.IsMatch(q))
        {
            if (!src.Bool("spotify")) throw new ResolveException("ссылки Spotify отключены");
            return await SearchAny(await SpotifyQuery(q), "spotify", minViews);
        }
        var ym = YandexMusic.TrackRe.Match(q);
        if (ym.Success)
        {
            if (!src.Bool("yandex")) throw new ResolveException("ссылки Яндекс Музыки отключены");
            try { return FromYandex(await _hub.Yandex.Track(ym.Groups[1].Value), "yandex"); }
            catch (ResolveException) { throw; }
            catch (Exception e) { throw new ResolveException($"Яндекс Музыка: {e.Message}"); }
        }
        if (YtRe.IsMatch(q))
        {
            if (!src.Bool("youtube")) throw new ResolveException("ссылки YouTube отключены");
            return await YtByUrl(q, "youtube");
        }
        if (UrlRe.IsMatch(q)) throw new ResolveException("поддерживаются ссылки YouTube, Яндекс Музыки, Spotify или название трека");
        return await SearchAny(q, "search", minViews);
    }

    private string Banned(string text)
    {
        var low = text.ToLowerInvariant();
        return Cfg.StrList("banned_words").FirstOrDefault(w => w != "" && low.Contains(w.ToLowerInvariant()));
    }

    public async Task<(bool Ok, string Text, Track Track)> Request(string query, string name, string login,
        int priority = 0, bool manual = false, (string, string, int)? redemption = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return (false, "пустой заказ: укажите ссылку или название", null);
        if (!manual)
        {
            var blocked = Cfg.StrList("blocked_users").Select(u => u.ToLowerInvariant().TrimStart('@'));
            if (blocked.Contains(login.ToLowerInvariant())) return (false, "заказы музыки для вас ограничены", null);
            if (Banned(query) != null) return (false, "запрещённое слово в заказе", null);
            if (Queue.Count >= Cfg.Int("queue_limit", 15)) return (false, $"очередь заполнена ({Cfg.Int("queue_limit")})", null);
            if (Queue.Count(t => t.Login == login) >= Cfg.Int("per_user_limit", 2))
                return (false, $"лимит заказов от зрителя: {Cfg.Int("per_user_limit")}", null);
        }
        var minViews = manual ? 0L : (long)Cfg.Num("min_views", 0);
        Track track;
        try { track = await Resolve(query, minViews); }
        catch (ResolveException e) { return (false, e.Message, null); }
        catch (Exception e) { return (false, $"не удалось найти трек: {YtDlp.Explain(e.Message)}", null); }
        if (!manual)
        {
            var maxSec = Cfg.Int("max_duration_min", 8) * 60;
            if (track.Duration <= 0) return (false, "не удалось определить длительность", null);
            if (track.Duration > maxSec) return (false, $"трек длиннее {Cfg.Int("max_duration_min")} мин", null);
            if (Banned(track.Title) != null) return (false, "запрещённое слово в названии трека", null);
            // Фильтр просмотров — только для YouTube (у Яндекс Музыки просмотров нет)
            if (minViews > 0 && !track.Vid.StartsWith("ym:") && track.Views >= 0 && track.Views < minViews)
                return (false, $"у видео меньше {YtDlp.FormatViews(minViews)} просмотров", null);
            var bt = Cfg.StrList("blocked_tracks").Select(b => b.ToLowerInvariant()).ToList();
            if (bt.Contains(track.Vid.ToLowerInvariant()) || bt.Contains(track.Title.ToLowerInvariant()) ||
                bt.Contains(track.PageUrl.ToLowerInvariant()))
                return (false, "этот трек заблокирован", null);
        }
        track.Requester = name;
        track.Login = login;
        track.Priority = priority;
        track.Redemption = redemption;
        Queue.RemoveAll(q => q.Fallback);   // заранее выбранный резервный трек уступает заказу
        var idx = Queue.FindIndex(x => x.Priority < priority);
        if (idx < 0) idx = Queue.Count;
        Queue.Insert(idx, track);
        // заказ важнее резервного плейлиста: прерываем фоновый трек
        if (Current?.Fallback == true && Cfg.Bool("fallback_interrupt", true)) Skip("пришёл заказ");
        Wake();
        Prefetch();
        if (!manual) PlayChime();
        return (true, $"«{track.Title}» добавлен в очередь (позиция {idx + 1})", track);
    }

    public object State()
    {
        var cur = Current;
        object curObj = null;
        if (cur != null)
            curObj = new
            {
                id = cur.Id, vid = cur.Vid, title = cur.Title, page_url = cur.PageUrl, duration = cur.Duration,
                requester = cur.Requester, login = cur.Login, priority = cur.Priority, source = cur.Source,
                paid = cur.Redemption != null, position = Math.Round(Position, 1), paused = IsPaused,
                gain_db = GainDb(cur.Vid), in_obs = _obsActive,
                loading = Loading, views = cur.Views, liked = IsLiked(cur.Vid),
            };
        return new
        {
            current = curObj,
            queue = Queue.Select(t => t.Public()).ToList(),
            volume = Cfg.Int("volume", 60),
            votes = Votes.Count,
            votes_needed = Cfg.Int("skip_votes", 3),
            toast = new { id = ToastId, text = ToastText },
            output = Cfg.Str("output", "app"),
            fallback_status = FallbackStatus,
            liked_count = LikedItems.Count,
            restored = ActionLog.Now - RestoredAt < 60 ? RestoredInfo : "",
            obs_connected = ObsConnected,
        };
    }
}
