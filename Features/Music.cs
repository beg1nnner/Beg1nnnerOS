using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
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
    public string Artist = "";        // исполнитель(и), если источник его знает (YouTube Music, Яндекс)
    public string Channel = "";       // канал YouTube
    public string ChannelId = "";     // UC… и @псевдоним канала — для чёрного списка
    public string Cover = "";         // обложка (Яндекс); у YouTube берётся превью видео по ID
    public long Views = -1;   // просмотры на YouTube (-1 — неизвестно)
    public (string RewardId, string RedemptionId, int Cost)? Redemption;
    /// <summary>Покупка за баллы: "" — не оплачен баллами, pending — ждёт (баллы ещё можно вернуть), done — подтверждена, refunded — возвращена.</summary>
    public string Pay = "";
    public bool Fallback;   // трек из резервного плейлиста (играет, когда заказов нет)
    public double ResumeAt;   // с какой секунды начать (восстановление после вылета)
    public OrderTiming Timing;   // замер шагов заказа (не сохраняется)
    public YtJob Job;            // идущая загрузка yt-dlp (не сохраняется)

    public object Public() => new
    {
        id = Id, vid = Vid, title = Title, page_url = PageUrl, duration = Duration, requester = Requester,
        login = Login, priority = Priority, source = Source, paid = Redemption != null, pay = Pay, views = Views, fallback = Fallback,
        artist = string.Join(", ", Music.ArtistsOf(this)), channel = Channel, cover = Music.CoverOf(this),
    };

    public Track Clone() => new()
    {
        Vid = Vid, Title = Title, PageUrl = PageUrl, Duration = Duration, Source = Source, Artist = Artist, Channel = Channel,
        ChannelId = ChannelId, Cover = Cover, Views = Views,
    };
}

/// <summary>Время шагов одного заказа (с.): от выкупа награды до звука. -1 — шага не было.</summary>
public sealed class OrderTiming
{
    public readonly Stopwatch Sw = Stopwatch.StartNew();
    public double Priority = -1, Search = -1, Info = -1, Launch = -1, Download = -1, Loudness = -1, Player = -1, Total = -1;
    public string Cache = "";        // что взято из кэша: запрос, сведения, файл
    public bool Immediate;           // очередь заказов была пуста — время «заказ → звук» без ожидания других треков
    public bool Logged;
}

/// <summary>Кто пропустил трек — от этого зависит, вернутся ли баллы.</summary>
public enum SkipBy { Staff, Vote, Requester, Reward, Block, System }

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

    // ---------- кэш поиска и сведений ----------
    private readonly Dictionary<string, string> _qCache = new();            // нормализованный запрос → ID ролика
    private readonly LinkedList<string> _qOrder = new();
    private readonly Dictionary<string, (Track T, double Ts)> _infoCache = new();   // ID ролика → сведения (6 ч)
    private readonly SemaphoreSlim _speculative = new(2, 2);   // не больше двух опережающих загрузок на всю программу

    // ---------- выравнивание громкости ----------
    // Измерения по ID трека: громкость (LUFS) и пик. Хранятся в кэше, чтобы не мерить трек дважды.
    private readonly ConcurrentDictionary<string, double[]> _loud = new();
    private readonly ConcurrentDictionary<string, double[]> _loudPartial = new();   // по первым 25 секундам — пока нет полного
    private readonly Dictionary<string, Task> _measuring = new();
    private double _obsRampUntil;
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
        DropBlocked();
    }

    /// <summary>В панели заблокировали трек, исполнителя или канал — такие заказы уходят из очереди, баллы возвращаются.</summary>
    private void DropBlocked()
    {
        var drop = Queue.Where(q => !q.Fallback && q.Login != "" && (TrackBlocked(q) || BlockedBy(q) != null)).ToList();
        if (drop.Count == 0) return;
        foreach (var q in drop) { Queue.Remove(q); ForgetDownload(q); }
        _hub.Log.Add("music", $"Из очереди убрано заблокированных заказов: {drop.Count}");
        RefundMany(drop, "трек заблокирован стримером");
    }

    /// <summary>Поправка громкости трека в дБ: до целевого уровня, не больше +12 / −12 дБ.</summary>
    public double GainDb(string vid)
    {
        if (!Cfg.Bool("normalize", true)) return 0;
        // полное измерение; пока его нет — предварительное по началу трека
        if (!_loud.TryGetValue(vid, out var m) && !_loudPartial.TryGetValue(vid, out m)) return 0;
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

    /// <summary>
    /// Измеряет громкость скачанного файла в фоне: сначала первые 25 секунд (предварительная поправка, доли секунды),
    /// затем весь файл (~1 с; это значение и попадает в кэш). Старт трека ждёт только предварительную оценку
    /// (WaitQuickGain, не дольше 3 с); уточнение по всему файлу подтягивается плавно за 1,5 секунды.
    /// </summary>
    private Task StartMeasure(string vid, string path)
    {
        if (!Cfg.Bool("normalize", true) || _loud.ContainsKey(vid)) return Task.CompletedTask;
        if (_measuring.TryGetValue(vid, out var running)) return running;
        var task = MeasureBg(vid, path);
        _measuring[vid] = task;
        return task;
    }

    /// <summary>
    /// Перед стартом ждём быструю оценку громкости по началу трека (обычно доли секунды, на слабом ПК — до 2–3 с).
    /// Иначе новый трек первые секунды звучит без поправки (громкие треки — громко), а потом резко «проседает».
    /// Не успели за 3 с — трек начинается с плавного нарастания, за которое поправка успевает подтянуться.
    /// </summary>
    private async Task WaitQuickGain(Track t, string path)
    {
        if (!Cfg.Bool("normalize", true) || _loud.ContainsKey(t.Vid) || _loudPartial.ContainsKey(t.Vid)) return;
        _ = StartMeasure(t.Vid, path);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 && _measuring.ContainsKey(t.Vid)
               && !_loud.ContainsKey(t.Vid) && !_loudPartial.ContainsKey(t.Vid))
            await Task.Delay(40);
        if (t.Timing != null) t.Timing.Loudness = sw.Elapsed.TotalSeconds;
        if (!_loud.ContainsKey(t.Vid) && !_loudPartial.ContainsKey(t.Vid))
        {
            _nextFade = Math.Max(_nextFade, 2.5);
            _hub.Log.Add("skip", $"Громкость «{Short(t.Title, 60)}» ещё измеряется — трек начнётся с плавного нарастания");
        }
    }

    private async Task MeasureBg(string vid, string path)
    {
        try
        {
            var part = await Task.Run(() => Loudness.Measure(path, maxSeconds: 25));
            if (part != null && !_loud.ContainsKey(vid)) { _loudPartial[vid] = new[] { part.Value.Lufs, part.Value.Peak }; GainChanged(vid); }
            var m = await Task.Run(() => Loudness.Measure(path));
            if (m == null) return;
            _loud[vid] = new[] { m.Value.Lufs, m.Value.Peak };
            _loudPartial.TryRemove(vid, out _);
            _loudDirty = true;
            SaveLoudness();
            GainChanged(vid);
        }
        catch (Exception e) { _hub.Log.Add("skip", $"Не удалось измерить громкость: {e.Message}"); }
        finally { _measuring.Remove(vid); }
    }

    /// <summary>Громкость трека измерена, пока он уже играет: плавно подводим поправку (без щелчка).</summary>
    private void GainChanged(string vid)
    {
        if (Current == null || Current.Vid != vid || Loading) return;
        var g = GainDb(vid);
        if (_obsActive) _obsRampUntil = ActionLog.Now + 2;   // OBS-страница сама плавно меняет громкость
        else if (Math.Abs(Player.GainDb - g) >= 0.1) Player.RampGain(g, 1.5);
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
        YtDlp.CleanPartial(Config.CacheDir);   // недокачанное при прошлом запуске
        TrimCache();
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
        ["cost"] = t.Redemption?.Cost ?? 0, ["pay"] = t.Pay, ["position"] = Math.Round(pos, 1), ["cover"] = t.Cover,
        ["artist"] = t.Artist, ["channel"] = t.Channel,
    };

    private static Track TrackFrom(JsonNode n) => new()
    {
        Vid = n.Str("vid"), Title = n.Str("title"), PageUrl = n.Str("url"), Duration = n.Int("duration"),
        Requester = n.Str("requester"), Login = n.Str("login"), Priority = n.Int("priority"), Source = n.Str("source", "youtube"),
        Views = (long)n.Num("views", -1), ResumeAt = n.Num("position"), Cover = n.Str("cover"),
        Redemption = n.Str("redemption_id") != "" ? (n.Str("reward_id"), n.Str("redemption_id"), n.Int("cost")) : null,
        // до 2.7.1 покупка подтверждалась сразу при заказе — сохранённые без состояния считаем подтверждёнными
        Pay = n.Str("redemption_id") == "" ? "" : n.Str("pay", "done"),
        Artist = n.Str("artist"), Channel = n.Str("channel"),
    };

    /// <summary>Записывает очередь и текущий трек. Заказы резервного плейлиста не сохраняются — они и так вернутся.</summary>
    public void SaveQueue(bool force = false)
    {
        try
        {
            var cur = Current != null && !Current.Fallback ? Current : null;
            var queue = Queue.Where(t => !t.Fallback).ToList();
            var sig = (cur?.Id ?? "") + ":" + cur?.Pay + "|" + string.Join(",", queue.Select(t => t.Id + ":" + t.Priority + ":" + t.Pay)) + "|" + _recent.Count;
            var pos = cur == null ? 0 : Loading ? cur.ResumeAt : Position;   // пока трек грузится, плеер ещё не на нём
            // очередь не менялась — позицию текущего трека обновляем раз в 10 секунд
            if (!force && sig == _savedSig && (cur == null || Math.Abs(pos - _savedPos) < 10)) return;
            var js = new JsonObject
            {
                ["saved"] = ActionLog.Now,
                ["current"] = cur != null ? TrackJson(cur, pos) : null,
                ["queue"] = new JsonArray(queue.Select(t => (JsonNode)TrackJson(t)).ToArray()),
                ["recent"] = new JsonArray(_recent.Select(r => (JsonNode)new JsonObject
                {
                    ["vid"] = r.Vid, ["keys"] = string.Join("\n", r.Keys), ["ts"] = r.Ts, ["order"] = r.Order,
                }).ToArray()),
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
            // недавно сыгранные заказы — для защиты от повторов (переживают перезапуск независимо от восстановления очереди)
            foreach (var r in js["recent"] as JsonArray ?? new JsonArray())
                if (ActionLog.Now - r.Num("ts") < 24 * 3600)
                    _recent.Add(new RecentPlay(r.Str("vid"), r.Str("keys").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(), r.Num("ts"), r.Bool("order")));
            var age = ActionLog.Now - js.Num("saved");
            var maxH = Math.Clamp(Cfg.Int("restore_hours", 12), 1, 168);
            var skipRestore = !Cfg.Bool("restore_queue", true) || age > maxH * 3600;
            if (skipRestore)
            {
                // очередь не возвращаем — баллы за неподтверждённые заказы возвращаем зрителям
                var saved = new List<Track>();
                if (js["current"] is JsonObject c0 && c0.Str("vid") != "") saved.Add(TrackFrom(c0));
                foreach (var n in js["queue"] as JsonArray ?? new JsonArray()) if (n.Str("vid") != "") saved.Add(TrackFrom(n));
                var pending = saved.Where(t => t.Pay == "pending").ToList();
                if (saved.Count > 0 && Cfg.Bool("restore_queue", true))
                    _hub.Log.Add("music", $"Старая очередь (сохранена {(int)(age / 3600)} ч назад) не восстановлена — больше {maxH} ч");
                if (pending.Count > 0)
                {
                    RefundMany(pending, "очередь не восстановлена после перезапуска программы");
                    SaveQueue(true);
                }
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

    // ---------- баллы: списываются только за сыгранный трек ----------
    // Покупка остаётся на Twitch «ожидающей», пока трек не отыграл 30 секунд (или не закончился раньше).
    // Пока она не подтверждена, баллы можно вернуть: трек не загрузился, его убрали, отменили или заблокировали.
    public const double PaySeconds = 30;

    /// <summary>Подтверждает покупку (баллы списаны окончательно). Повторный вызов ничего не делает.</summary>
    public void ConfirmPay(Track t)
    {
        if (t?.Redemption == null || t.Pay != "pending") return;
        t.Pay = "done";
        SaveQueue(true);
        var (rid, redId, cost) = t.Redemption.Value;
        try { _hub.Stats.Music(_hub.Stream.Key, "paid", "ok", t.Login, t.Requester, t.Title, cost); } catch { }
        _ = _hub.Rewards.Settle(rid, redId, true, t.Requester, t.Title, cost);
    }

    /// <summary>Возвращает баллы за заказ, если покупка ещё не подтверждена. tell — написать зрителю в чат.</summary>
    public bool RefundPay(Track t, string reason, bool tell = true)
    {
        if (t?.Redemption == null || t.Pay != "pending") return false;
        t.Pay = "refunded";
        SaveQueue(true);
        var (rid, redId, cost) = t.Redemption.Value;
        try { _hub.Stats.Music(_hub.Stream.Key, "refund", "refunded", t.Login, t.Requester, t.Title, cost); } catch { }
        _hub.Log.Add("music", $"Баллы возвращены {t.Requester} за «{t.Title}» ({cost}): {reason}");
        _ = _hub.Rewards.Settle(rid, redId, false, t.Requester, t.Title, cost);
        if (tell && t.Requester != "" && Cfg.Bool("chat_errors", true))
            _ = SayChat($"@{t.Requester}, {reason}: «{Short(t.Title, 60)}». Баллы возвращены");
        return true;
    }

    /// <summary>Несколько возвратов сразу (очистка очереди) — одно общее сообщение в чат.</summary>
    public int RefundMany(IEnumerable<Track> tracks, string reason)
    {
        var done = tracks.Where(t => RefundPay(t, reason, false)).ToList();
        if (done.Count > 0 && Cfg.Bool("chat_errors", true))
        {
            var who = string.Join(", ", done.Select(t => "@" + t.Requester).Distinct().Take(15));
            _ = SayChat($"{char.ToUpper(reason[0])}{reason[1..]} — баллы возвращены: {who}");
        }
        return done.Count;
    }

    private async Task SayChat(string text)
    {
        try { await _hub.Twitch.SendChat(text); }
        catch (Exception e) { _hub.Log.Add("skip", $"Не удалось написать в чат: {e.Message}"); }
    }

    /// <summary>Трек перестал играть: что делать с покупкой зависит от того, кто и когда его остановил.</summary>
    private void SettleOnStop(Track t, SkipBy by)
    {
        if (t?.Pay != "pending") return;
        var pos = Loading && Current == t ? 0 : Position;
        if (by == SkipBy.Block) RefundPay(t, "трек заблокирован");
        else if (by == SkipBy.Staff && pos < PaySeconds && Cfg.Bool("refund_early_skip", true)) RefundPay(t, "стример пропустил трек");
        else ConfirmPay(t);   // голосование, «Пропуск» другого зрителя, сам заказчик, больше 30 секунд
    }

    // ---------- защита от повторов ----------
    public sealed record RecentPlay(string Vid, HashSet<string> Keys, double Ts, bool Order);
    private readonly List<RecentPlay> _recent = new();

    private static readonly Regex Variant = new(
        @"\b(live|концерт\w*|лайв|remix|rmx|ремикс|cover|кавер|acoustic|акусти\w*|slowed|замедл\w*|sped\s+up|speed\s+up|ускор\w*|nightcore|найткор|reverb|instrumental|минус|karaoke|караоке)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string NormText(string s) =>
        Regex.Replace((s ?? "").ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+", " ").Trim();

    /// <summary>
    /// Ключи «исполнитель — название» для сравнения: без «(Official Video)» и знаков, «ё» → «е».
    /// Концертная версия, ремикс, кавер и т. п. дают другой ключ — это не повтор оригинала.
    /// </summary>
    public static HashSet<string> RepeatKeys(Track t)
    {
        var keys = new HashSet<string>();
        var clean = CleanTitle(t.Title);
        var title = NormText(clean);
        var variants = string.Join(",", Variant.Matches(title).Select(m => m.Value.Length > 4 ? m.Value[..4] : m.Value).Distinct().OrderBy(x => x));
        void Add(string k) { if (k.Length >= 4) keys.Add(k + "|" + variants); }
        if (TitleArtist.IsMatch(clean)) Add(title);   // в названии уже есть исполнитель
        var artist = t.Artist != "" ? t.Artist : Regex.Replace(t.Channel ?? "", @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
        if (artist != "" && !title.StartsWith(NormText(artist))) Add(NormText(artist) + " " + title);
        if (keys.Count == 0) Add(title);
        return keys;
    }

    private bool SameTrack(Track a, string vid, HashSet<string> keys) => vid != "" && a.Vid == vid || keys.Overlaps(RepeatKeys(a));

    /// <summary>Причина отказа, если этот трек играет, уже стоит в очереди или играл недавно; иначе null.</summary>
    public string RepeatReason(Track t) => RepeatReason(t.Vid, RepeatKeys(t));

    public string RepeatReason(string vid, HashSet<string> keys)
    {
        if (Current != null && SameTrack(Current, vid, keys)) return "этот трек уже играет";
        var i = Queue.FindIndex(x => !x.Fallback && SameTrack(x, vid, keys));
        if (i >= 0) return $"этот трек уже в очереди (№{i + 1})";
        var mins = Math.Clamp(Cfg.Int("repeat_minutes", 30), 0, 1440);
        if (mins > 0)
        {
            var r = _recent.LastOrDefault(x => x.Order && ActionLog.Now - x.Ts < mins * 60 && (vid != "" && x.Vid == vid || x.Keys.Overlaps(keys)));
            if (r != null)
            {
                var ago = Math.Max(1, (int)Math.Round((ActionLog.Now - r.Ts) / 60));
                return $"этот трек играл {ago} {L.Pl(ago, "минуту", "минуты", "минут")} назад, закажите его позже";
            }
        }
        return null;
    }

    public static bool IsRepeatText(string text) => text.StartsWith("этот трек уже") || text.StartsWith("этот трек играл");

    private void RememberPlayed(Track t)
    {
        _recent.Add(new RecentPlay(t.Vid, RepeatKeys(t), ActionLog.Now, !t.Fallback && t.Login != ""));
        _recent.RemoveAll(x => ActionLog.Now - x.Ts > 24 * 3600);
        if (_recent.Count > 500) _recent.RemoveRange(0, _recent.Count - 500);
    }

    // ---------- кэш звуковых файлов ----------
    /// <summary>Готовый файл трека в кэше (не пустой) или null.</summary>
    private static string CachedFile(string vid)
    {
        if (string.IsNullOrEmpty(vid)) return null;
        try
        {
            var name = vid.StartsWith("ym:") ? "ym_" + vid[3..] + ".mp3" : null;
            if (name != null) { var p = Path.Combine(Config.CacheDir, name); return File.Exists(p) && new FileInfo(p).Length > 0 ? p : null; }
            return Directory.GetFiles(Config.CacheDir, $"yt_{vid}.*").FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") && new FileInfo(f).Length > 0);
        }
        catch { return null; }
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private static string VidOfFile(string name)
    {
        var n = Path.GetFileNameWithoutExtension(name);
        return n.StartsWith("yt_") ? n[3..] : n.StartsWith("ym_") ? "ym:" + n[3..] : "";
    }

    private static IEnumerable<FileInfo> AudioFiles()
    {
        try { return new DirectoryInfo(Config.CacheDir).GetFiles().Where(f => f.Name.StartsWith("yt_") || f.Name.StartsWith("ym_")).ToList(); }
        catch { return Array.Empty<FileInfo>(); }
    }

    /// <summary>
    /// Кэш музыки не больше заданного объёма: сначала уходят давно не игравшие файлы, последними — треки из
    /// понравившихся и резервного плейлиста. Лимит 0 — как раньше: удаляются файлы старше суток.
    /// </summary>
    private void TrimCache()
    {
        try
        {
            var inUse = new HashSet<string>(Queue.Select(q => q.Vid));
            if (Current != null) inUse.Add(Current.Vid);
            foreach (var k in _downloads.Where(p => !p.Value.IsCompleted).Select(p => p.Key)) inUse.Add(k);
            var files = AudioFiles().Where(f => !inUse.Contains(VidOfFile(f.Name))).ToList();
            var limitMb = Math.Max(0, Cfg.Int("cache_mb", 2048));
            if (limitMb == 0)
            {
                foreach (var f in files) if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) { try { f.Delete(); } catch { } }
                return;
            }
            var total = AudioFiles().Sum(f => f.Length);
            var limit = limitMb * 1024L * 1024;
            if (total <= limit) return;
            var keep = new HashSet<string>(LikedItems.Select(x => x.Str("vid")).Concat(_fbList.Select(t => t.Vid)));
            foreach (var f in files.OrderBy(f => keep.Contains(VidOfFile(f.Name)) ? 1 : 0).ThenBy(f => f.LastWriteTimeUtc))
            {
                if (total <= limit) break;
                try { var len = f.Length; f.Delete(); total -= len; _downloads.Remove(VidOfFile(f.Name)); } catch { }
            }
        }
        catch (Exception e) { _hub.Log.Add("skip", "Кэш музыки: " + e.Message); }
    }

    public (long Used, long Limit) CacheInfo() => (AudioFiles().Sum(f => f.Length), Math.Max(0, Cfg.Int("cache_mb", 2048)) * 1024L * 1024);

    /// <summary>«Очистить»: удаляет все файлы кэша, кроме играющего, стоящих в очереди и скачиваемых.</summary>
    public long ClearCache()
    {
        var inUse = new HashSet<string>(Queue.Select(q => q.Vid));
        if (Current != null) inUse.Add(Current.Vid);
        foreach (var k in _downloads.Where(p => !p.Value.IsCompleted).Select(p => p.Key)) inUse.Add(k);
        long freed = 0;
        foreach (var f in AudioFiles().Where(f => !inUse.Contains(VidOfFile(f.Name))))
        {
            try { var len = f.Length; f.Delete(); freed += len; _downloads.Remove(VidOfFile(f.Name)); } catch { }
        }
        _hub.Log.Add("music", $"Кэш музыки очищен: освобождено {freed / 1048576.0:0.#} МБ");
        return freed;
    }

    /// <summary>Убирает отменённую загрузку: процесс yt-dlp завершается, недокачанный файл удаляется.</summary>
    private void ForgetDownload(Track t)
    {
        if (t == null || t.Vid == "") return;
        if (Current?.Vid == t.Vid || Queue.Any(q => q.Vid == t.Vid)) return;   // этот же трек ещё нужен
        if (_downloads.TryGetValue(t.Vid, out var task) && !task.IsCompleted)
        {
            t.Job?.Cancel();
            _downloads.Remove(t.Vid);
        }
    }

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

    /// <summary>Ближайшие треки резервного плейлиста (для панели).</summary>
    public List<object> UpcomingFallback(int n)
    {
        var pool = FallbackPool();
        var liked = new HashSet<string>(LikedItems.Select(x => x.Str("vid")));
        var ids = _fbOrder.Where(pool.ContainsKey).Take(n).ToList();
        if (ids.Count < n) ids.AddRange(pool.Keys.Where(v => !ids.Contains(v) && !_fbBad.Contains(v)).Take(n - ids.Count));
        return ids.Select(v => (object)new { title = pool[v].Title, duration = pool[v].Duration, liked = liked.Contains(v) && !_fbList.Any(t => t.Vid == v) }).ToList();
    }

    private Dictionary<string, Track> FallbackPool()
    {
        var pool = new Dictionary<string, Track>();
        foreach (var t in _fbList) pool.TryAdd(t.Vid, t);
        if (LikedInFallback)
            foreach (var x in LikedItems)
                pool.TryAdd(x.Str("vid"), new Track
                {
                    Vid = x.Str("vid"), Title = x.Str("title"), PageUrl = x.Str("url"), Duration = x.Int("duration"),
                    Source = x.Str("source", "youtube"),
                });
        return pool;
    }

    private Track NextFallback()
    {
        // общий пул: плейлист YouTube + понравившиеся треки (без повторов)
        var pool = FallbackPool();
        _fbOrder.RemoveAll(v => !pool.ContainsKey(v));
        if (_fbOrder.Count == 0)
        {
            var ids = pool.Keys.Where(v => !_fbBad.Contains(v)).ToList();
            if (Cfg.Bool("fallback_shuffle", true)) ids = ids.OrderBy(_ => Random.Shared.Next()).ToList();
            _fbOrder.AddRange(ids);
        }
        if (_fbOrder.Count == 0) return null;
        // не ставим трек, который играл или был заказан за последние N минут, если в плейлисте есть другие
        var mins = Math.Clamp(Cfg.Int("repeat_minutes", 30), 0, 1440);
        var idx = 0;
        if (mins > 0)
        {
            var recent = _recent.Where(x => ActionLog.Now - x.Ts < mins * 60).ToList();
            var cands = recent.Count == 0 ? -1 : _fbOrder.FindIndex(v => !recent.Any(r => r.Vid == v || r.Keys.Overlaps(RepeatKeys(pool[v]))));
            if (cands > 0) idx = cands;
        }
        var vid = _fbOrder[idx];
        _fbOrder.RemoveAt(idx);
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
            if (Current?.Fallback == true) Skip("резервный плейлист выключен", SkipBy.System);
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
        return $"{name}, «{Short(t.Title, 50)}» — №{idx + 1} в очереди, заиграет через {Mins(SecondsUntil(idx))}{extra}";
    }

    public string CancelOwn(string login, string name)
    {
        var idx = Queue.FindLastIndex(t => t.Login == login && !t.Fallback);
        if (idx < 0)
            return Current != null && Current.Login == login && Current.Pay != ""
                ? $"{name}, играющий трек отменить нельзя. Баллы за заказ не возвращаются"
                : $"{name}, в очереди нет ваших треков (играющий трек отменить нельзя)";
        var t = Queue[idx];
        Queue.RemoveAt(idx);
        ForgetDownload(t);
        _hub.Log.Add("music", $"{login} отменил свой заказ «{t.Title}»");
        var paid = RefundPay(t, "заказ отменён", false) ? " Баллы возвращены." : "";
        return $"{name}, «{Short(t.Title, 50)}» убран из очереди.{paid}";
    }

    /// <summary>Скачивает трек (если его ещё нет в кэше) и запускает измерение громкости в фоне.</summary>
    private async Task<string> DownloadAudio(Track t)
    {
        var sw = Stopwatch.StartNew();
        var p = await DownloadRaw(t);
        AfterDownload(t, p, t.Job != null && t.Job.DownloadSec > 0 ? t.Job.DownloadSec : sw.Elapsed.TotalSeconds);
        return p;
    }

    /// <summary>Загрузка, начатая ещё при заказе (сведения и звук одним запуском yt-dlp).</summary>
    private async Task<string> FromJob(Track t, YtJob job)
    {
        var sw = Stopwatch.StartNew();
        var p = await job.File;
        EnsureComplete(t, p);
        AfterDownload(t, p, job.DownloadSec > 0 ? job.DownloadSec : sw.Elapsed.TotalSeconds);
        return p;
    }

    // ---------- проверка, что файл скачан целиком ----------
    /// <summary>Длина звукового файла, с; −1 — не удалось прочитать (это решит плеер при запуске).</summary>
    private static double FileSeconds(string path)
    {
        try
        {
            using var r = new NAudio.Wave.MediaFoundationReader(path);
            return r.TotalTime.TotalSeconds;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Файл заметно короче трека: обрыв загрузки (вылет программы, выключение ПК, сбой сети)
    /// или 30-секундный отрывок вместо песни. Короткие треки и треки без известной длины не проверяем.
    /// </summary>
    private static bool IsTruncated(Track t, double fileSec) =>
        t.Duration >= 40 && fileSec > 0 && fileSec < t.Duration * 0.85 && t.Duration - fileSec > 8;

    /// <summary>Проверяет свежескачанный файл. Обрезанный удаляется, а загрузка считается неудачной.</summary>
    private void EnsureComplete(Track t, string path)
    {
        var sec = FileSeconds(path);
        if (!IsTruncated(t, sec)) return;
        try { File.Delete(path); } catch { }
        _hub.Log.Add("skip", $"Файл «{Short(t.Title, 60)}» короче трека ({(int)sec} с из {t.Duration} с) — удалён");
        throw new ResolveException(t.Vid.StartsWith("ym:") && sec <= 35
            ? "в Яндекс Музыке доступен только отрывок (нужна подписка Плюс)"
            : "файл скачался не полностью");
    }

    // ---------- уведомления в панели ----------
    private int _noticeId;
    private string _noticeText = "";

    /// <summary>Сообщение стримеру в панели (всплывающее, с пометкой ошибки) — чтобы трек не пропадал молча.</summary>
    private void Notice(string text)
    {
        _noticeId++;
        _noticeText = text;
    }

    private void AfterDownload(Track t, string path, double sec)
    {
        Touch(path);
        if (t.Timing != null && t.Timing.Download < 0) t.Timing.Download = sec;
        _ = StartMeasure(t.Vid, path);
        TrimCache();
    }

    private async Task<string> DownloadRaw(Track t)
    {
        Directory.CreateDirectory(Config.CacheDir);
        var cached = CachedFile(t.Vid);
        if (cached != null && IsTruncated(t, FileSeconds(cached)))
        {
            // обрезанный файл от прошлых версий или после сбоя — иначе трек обрывался бы при каждом заказе
            _hub.Log.Add("skip", $"В кэше обрезанный файл «{Short(t.Title, 60)}» — скачиваю заново");
            try { File.Delete(cached); } catch { }
            cached = null;
        }
        if (cached != null)
        {
            if (t.Timing != null) { t.Timing.Download = 0; t.Timing.Cache += (t.Timing.Cache == "" ? "" : ", ") + "файл"; }
            return cached;
        }
        if (t.Vid.StartsWith("ym:"))
        {
            var path = Path.Combine(Config.CacheDir, "ym_" + t.Vid[3..] + ".mp3");
            var url = await _hub.Yandex.DirectLink(t.Vid[3..]);
            // пишем потоком во временный файл; в кэш попадает только полностью скачанный
            var tmp = Path.Combine(Config.CacheDir, "ym_" + t.Vid[3..] + ".part.mp3");   // .mp3 в конце — чтобы файл открылся для проверки длины
            try
            {
                using (var resp = await _hub.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    var expected = resp.Content.Headers.ContentLength;
                    await using (var src = await resp.Content.ReadAsStreamAsync())
                    await using (var dst = File.Create(tmp))
                        await src.CopyToAsync(dst);
                    if (expected is > 0 && new FileInfo(tmp).Length != expected)
                        throw new ResolveException("файл скачался не полностью");
                }
                EnsureComplete(t, tmp);
                File.Move(tmp, path, true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            return path;
        }
        // сведения и звук — одним запуском yt-dlp (раньше сведения запрашивались ещё раз перед скачиванием)
        var job = _hub.YtDlp.Fetch(t.PageUrl != "" ? t.PageUrl : "https://www.youtube.com/watch?v=" + t.Vid, t.Source, Config.CacheDir);
        t.Job = job;
        var file = await job.File;
        EnsureComplete(t, file);
        return file;
    }

    private async Task PlayNext()
    {
        var t = Queue[0];
        Queue.RemoveAt(0);
        Current = t;
        Votes.Clear();
        var path = await LoadForPlay(t);
        if (path == null || Current != t) return;
        await WaitQuickGain(t, path);
        if (Current != t) return;
        var ok = TryStart(t, path, out var err);
        if (!ok)
        {
            // повреждённый файл в кэше не должен ломать заказ: удаляем и скачиваем заново (один раз)
            _hub.Log.Add("skip", $"Файл «{t.Title}» не открылся ({err}) — скачиваю заново");
            try { File.Delete(path); } catch { }
            _downloads.Remove(t.Vid);
            t.Job = null;
            path = await LoadForPlay(t);
            if (path == null || Current != t) return;
            ok = TryStart(t, path, out err);
        }
        if (!ok)
        {
            _hub.Log.Error($"Плеер не смог открыть «{t.Title}»: файл повреждён или формат не поддерживается ({err})");
            Current = null;
            Wake();
            RefundPay(t, "не удалось открыть звук этого трека", false);
            Notice($"Трек «{Short(t.Title, 60)}» пропущен — файл не открылся ({err})" + (t.Pay == "refunded" ? ". Баллы зрителю возвращены" : ""));
            await TellViewer(t, "не удалось открыть звук этого трека");
            return;
        }
        try
        {
            var url = t.PageUrl != "" ? t.PageUrl : t.Vid.StartsWith("ym:") ? "" : $"https://www.youtube.com/watch?v={t.Vid}";
            _hub.Stats.Played(_hub.Stream.Key, t.Vid, t.Title, url, t.Source, t.Requester, t.Login, t.Duration, t.Fallback);
        }
        catch (Exception e) { _hub.Log.Add("skip", "История треков: " + e.Message); }
        RememberPlayed(t);
        _hub.Log.Add("music", $"Играет: {t.Title}" + (t.Requester != "" ? $" (заказ {t.Requester})" : ""));
        ShowToast($"♪ {t.Title}");
        LogTiming(t);
        Prefetch();
    }

    /// <summary>Загрузка трека перед стартом. null — не получилось (с баллами и сообщением зрителю уже разобрались) или трек пропущен.</summary>
    private async Task<string> LoadForPlay(Track t)
    {
        Loading = true;
        try
        {
            if (!t.Vid.StartsWith("ym:") && _hub.YtDlp.Installing)
                _hub.Log.Add("music", "Готовлю плеер к первому запуску (загрузка yt-dlp)…");
            return await GetAudio(t);
        }
        catch (Exception e)
        {
            _downloads.Remove(t.Vid);
            if (Current != t) return null;   // пропущен во время загрузки — загрузка отменена, баллы уже обработаны
            var reason = Reason(e);
            // YouTube не отдал трек — пробуем найти этот же трек в Яндекс Музыке
            var (path, ymWhy) = await YandexInstead(t, reason);
            if (path != null) return path;
            Loading = false;
            // причина целиком: и почему не YouTube, и почему не помогла Яндекс Музыка
            var full = ymWhy != null ? $"YouTube: {reason}; Яндекс Музыка: {ymWhy}" : reason;
            _hub.Log.Error($"Не удалось загрузить «{t.Title}»: {full}");
            if (Current == t) Current = null;
            Wake();
            RefundPay(t, full, false);
            Notice($"Трек «{Short(t.Title, 60)}» пропущен — {full}" + (t.Pay == "refunded" ? ". Баллы зрителю возвращены" : ""));
            await TellViewer(t, full);
            return null;
        }
        finally { Loading = false; }
    }

    private bool TryStart(Track t, string path, out string error)
    {
        error = "";
        try
        {
            var startAt = t.ResumeAt > 3 && (t.Duration <= 0 || t.ResumeAt < t.Duration - 5) ? t.ResumeAt - 2 : 0;
            var sw = Stopwatch.StartNew();
            StartPlayback(t, path, startAt);
            t.ResumeAt = 0;
            if (t.Timing != null && t.Timing.Player < 0) t.Timing.Player = sw.Elapsed.TotalSeconds;
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    // ---------- замеры времени заказа ----------
    private readonly List<(string Stream, double Sec)> _startTimes = new();
    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
    private static string Sec(double x) => x.ToString("0.0", Ru) + " с";

    /// <summary>Строка в журнал (тип «Диагностика»): сколько занял каждый шаг заказа до звука.</summary>
    private void LogTiming(Track t)
    {
        var tm = t.Timing;
        if (tm == null || tm.Logged) return;
        tm.Logged = true;
        tm.Total = tm.Sw.Elapsed.TotalSeconds;
        if (tm.Loudness < 0) tm.Loudness = 0;   // старт ждёт только быструю оценку громкости (WaitQuickGain)
        var parts = new List<string>();
        if (tm.Priority >= 0) parts.Add("приоритет " + Sec(tm.Priority));
        if (tm.Search >= 0) parts.Add("поиск " + Sec(tm.Search));
        if (tm.Info >= 0) parts.Add("сведения " + Sec(tm.Info));
        if (tm.Download >= 0) parts.Add("загрузка " + Sec(tm.Download));
        parts.Add("громкость " + Sec(tm.Loudness) + (_loud.ContainsKey(t.Vid) ? "" : " (в фоне)"));
        if (tm.Player >= 0) parts.Add("плеер " + Sec(tm.Player));
        parts.Add("всего " + Sec(tm.Total));
        var cache = tm.Cache != "" ? $" (из кэша: {tm.Cache})" : "";
        var wait = tm.Immediate ? "" : " (ждал своей очереди)";
        _hub.Log.Add("debug", $"Заказ «{Short(t.Title, 60)}»: {string.Join(" · ", parts)}{cache}{wait}");
        if (tm.Immediate)
        {
            _startTimes.Add((_hub.Stream.Key, tm.Total));
            if (_startTimes.Count > 500) _startTimes.RemoveAt(0);
        }
    }

    /// <summary>Среднее время «заказ → звук» за текущий стрим (только заказы в пустую очередь).</summary>
    public (double Avg, int Count) AvgStart()
    {
        var key = _hub.Stream.Key;
        var list = _startTimes.Where(x => x.Stream == key).Select(x => x.Sec).ToList();
        return list.Count == 0 ? (0, 0) : (Math.Round(list.Average(), 1), list.Count);
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

    /// <summary>
    /// Скачивает тот же трек из Яндекс Музыки. Path = null — не получилось; Why — почему (null, если Яндекс не пробовали:
    /// запасной источник выключен или Яндекс не подключён).
    /// </summary>
    private async Task<(string Path, string Why)> YandexInstead(Track t, string reason)
    {
        if (t.Vid.StartsWith("ym:") || !Cfg.Bool("yandex_fallback", true) || _hub.Yandex.Token == "") return (null, null);
        var query = CleanTitle(t.Title);
        if (query.Length < 2) return (null, null);
        var orig = (t.Vid, t.Title, t.PageUrl, t.Source, t.Duration);
        try
        {
            var y = await _hub.Yandex.Search(query);
            // длительность должна быть похожей — иначе это скорее другой трек (ремикс, кавер)
            if (t.Duration > 0 && y.Duration > 0 && Math.Abs(t.Duration - y.Duration) > Math.Max(25, t.Duration * 0.15))
            {
                _hub.Log.Add("skip", $"Яндекс Музыка: «{y.Title}» не похож по длительности на «{t.Title}» — не подменяю");
                return (null, "нашёлся только другой вариант трека");
            }
            if (Current != t) return (null, null);
            var oldTitle = t.Title;
            t.Vid = "ym:" + y.Id; t.Title = y.Title; t.PageUrl = y.Url; t.Source = "yandex";
            if (y.Duration > 0) t.Duration = y.Duration;
            Loading = true;
            var path = await GetAudio(t);
            _hub.Log.Add("music", $"YouTube не отдал «{oldTitle}» ({reason}) — играет из Яндекс Музыки: {y.Title}");
            Notice($"YouTube не отдал «{Short(oldTitle, 50)}» ({reason}) — играет из Яндекс Музыки");
            return (path, null);
        }
        catch (Exception e)
        {
            _hub.Log.Add("skip", $"Яндекс Музыка как запасной источник: {e.Message}");
            // подмена не удалась — возвращаем трек как был, чтобы в журнале и в чате было исходное название
            _downloads.Remove(t.Vid);
            (t.Vid, t.Title, t.PageUrl, t.Source, t.Duration) = orig;
            return (null, e is ResolveException or YandexException ? e.Message : "ошибка ответа Яндекс Музыки");
        }
    }

    /// <summary>Понятная причина ошибки на русском.</summary>
    public static string Reason(Exception e) => e is ResolveException ? e.Message : YtDlp.Explain(e.Message);

    /// <summary>Сообщает зрителю в чат, что его трек не удалось включить.</summary>
    private async Task TellViewer(Track t, string reason)
    {
        if (t.Login == "" || !Cfg.Bool("chat_errors", true)) return;
        var title = t.Title.Length > 80 ? t.Title[..80] + "…" : t.Title;
        var refund = t.Pay == "refunded" ? ". Баллы возвращены" : "";
        try { await _hub.Twitch.SendChat($"@{t.Requester}, не удалось включить «{title}»: {reason}. Трек пропущен{refund}"); }
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
        _curPath = path;
        _devRetries = 0;
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
            _curPath = _obsPath;
            _devRetries = 0;
            Player.Play(_obsPath, OnEnd, GainDb(t.Vid), _obsPos);
            if (paused) Player.SetPaused(true);
        }
        catch (Exception e)
        {
            _hub.Log.Error($"Плеер не смог открыть «{t.Title}»: {e.Message}");
            if (_obsPos < PaySeconds) RefundPay(t, "не удалось продолжить трек"); else ConfirmPay(t);
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
                // баллы за заказ списываются, когда трек отыграл первые 30 секунд
                if (cur is { Pay: "pending" } && !Loading && Position >= PaySeconds) ConfirmPay(cur);
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
                ConfirmPay(cur);   // трек доигран (короткий трек — до 30 секунд)
                HandOff(cf);
            }
            catch (Exception e) { _hub.Log.Add("skip", "Плавный переход: " + e.Message); }
        }
    }

    /// <summary>Текущий трек плавно уступает место следующему (он уже скачан): кроссфейд за fade секунд.</summary>
    private void HandOff(double fade)
    {
        _nextFade = fade;
        if (_obsActive) { _obsActive = false; OnEnd(_obsGen, ""); }
        else
        {
            Player.FadeOut(fade);
            Current = null;
            Wake();
        }
    }

    /// <summary>
    /// Пришёл заказ, а играет резервный трек: фон продолжает звучать, пока заказ не скачан, затем плавный переход.
    /// Не готов за 20 секунд — фон останавливается, как раньше. Загрузка не удалась — фон просто играет дальше.
    /// </summary>
    private async Task InterruptFallbackWhenReady(Track order, Track fb)
    {
        var dl = GetAudio(order);
        var ready = await Task.WhenAny(dl, Task.Delay(TimeSpan.FromSeconds(20))) == dl;
        if (Current != fb || Queue.Count == 0 || Queue[0].Fallback) return;   // фон уже закончился или заказ убрали
        if (ready && !dl.IsCompletedSuccessfully) return;
        if (!ready) { Skip("заказ не скачался за 20 секунд", SkipBy.System); return; }
        _xfadeGen = _obsActive ? _obsGen : Player.Gen;
        _hub.Log.Add("music", $"Резервный трек уступает заказу: {Queue[0].Title}");
        HandOff(CrossfadeSec > 0 ? CrossfadeSec : 0.6);
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
            ramp = ActionLog.Now < _obsRampUntil ? 1.5 : 0,   // громкость трека измерена после старта — менять плавно
        };
    }

    // ---------- устройство звука пропало посреди трека ----------
    private string _curPath = "";
    private int _devRetries;

    /// <summary>
    /// Воспроизведение оборвалось с ошибкой (выдернули наушники, отключили колонки, сменили устройство):
    /// продолжаем тот же трек с того же места — сначала на выбранном устройстве, затем на системном по умолчанию.
    /// true — трек продолжается.
    /// </summary>
    private bool ResumeAfterDeviceError(string error)
    {
        var t = Current;
        if (t == null || _obsActive || _devRetries >= 3 || string.IsNullOrEmpty(_curPath) || !File.Exists(_curPath)) return false;
        var pos = Player.Position;
        var paused = Player.Paused;
        var before = Player.DeviceName;
        _devRetries++;
        try
        {
            // первая попытка — снова «как в настройках» (если выбранного устройства больше нет, плеер сам возьмёт системное),
            // дальше — принудительно системное по умолчанию
            Player.Play(_curPath, OnEnd, GainDb(t.Vid), Math.Max(0, pos - 0.5), 0.3, useDefault: _devRetries > 1);
            if (paused) Player.SetPaused(true);
            var where = Player.DeviceName != before && before != "" ? $"«{before}» → «{Player.DeviceName}»" : $"«{Player.DeviceName}»";
            _hub.Log.Error($"Звук: устройство перестало отвечать ({error}). «{t.Title}» продолжается с {(int)pos} с: {where}");
            if (Player.DeviceName != before) Notice($"Устройство звука отключилось — музыка продолжается на «{Player.DeviceName}»");
            return true;
        }
        catch (Exception e)
        {
            _hub.Log.Add("skip", $"Звук: не удалось продолжить на другом устройстве ({e.Message})");
            return _devRetries < 3 && ResumeAfterDeviceError(error);
        }
    }

    private void OnEnd(int gen, string error)
    {
        if (gen != Player.Gen) return;
        if (error != "" && ResumeAfterDeviceError(error)) return;
        if (error != "")
        {
            _hub.Log.Error($"Плеер: воспроизведение прервалось ({error})");
            if (Current != null) Notice($"Трек «{Short(Current.Title, 60)}» прервался: нет рабочего устройства звука ({error})");
        }
        if (error == "") ConfirmPay(Current);   // доиграл до конца (короткий трек — раньше 30 секунд)
        else if (Current?.Pay == "pending" && Position < PaySeconds) RefundPay(Current, "воспроизведение прервалось");
        Current = null;
        Wake();
    }

    // ---------- управление ----------
    /// <summary>Пропуск текущего трека. by — кто пропустил: от этого зависит, вернутся ли баллы за заказ.</summary>
    public bool Skip(string reason = "", SkipBy by = SkipBy.Staff)
    {
        if (Current == null) return false;
        var title = Current.Title;
        var t = Current;
        SettleOnStop(t, by);
        if (CrossfadeSec > 0 && !_obsActive) Player.FadeOut(0.6); else Player.Stop();   // пропуск — короткое затухание без щелчка
        _nextFade = CrossfadeSec > 0 ? 0.6 : 0;
        _obsActive = false;
        Current = null;
        if (Loading) ForgetDownload(t);   // пропущен, пока скачивался — останавливаем загрузку
        _hub.Log.Add("music", $"Пропущен: {title}" + (reason != "" ? $" ({reason})" : ""));
        Wake();
        return true;
    }

    /// <summary>Поставить на паузу или снять с неё. Возвращает новое состояние паузы.</summary>
    public bool SetPaused(bool paused)
    {
        if (IsPaused != paused) TogglePause();
        return IsPaused;
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

    /// <summary>Стример или модератор убрал трек из очереди: загрузка останавливается, баллы возвращаются.</summary>
    public Track Remove(string id)
    {
        var t = Queue.FirstOrDefault(x => x.Id == id);
        if (t == null) return null;
        Queue.Remove(t);
        ForgetDownload(t);
        RefundPay(t, "трек убран из очереди");
        return t;
    }

    /// <summary>Очистить очередь заказов: баллы за все неподтверждённые заказы возвращаются одним сообщением.</summary>
    public int ClearQueue()
    {
        var removed = Queue.ToList();
        Queue.Clear();
        foreach (var t in removed) ForgetDownload(t);
        return RefundMany(removed, "очередь очищена");
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
            // модератор — как стример (ранний пропуск возвращает баллы); заказчик пропускает свой трек — баллы списываются
            Skip($"!skip от {msg.Login}", cur.Login != "" && msg.Login == cur.Login ? SkipBy.Requester : SkipBy.Staff);
            _hub.Stats.Music(_hub.Stream.Key, "vote_skip", "ok", msg.Login, msg.Name, cur.Title);
            return $"Трек «{cur.Title}» пропущен";
        }
        var need = Math.Max(1, Cfg.Int("skip_votes", 3));
        if (Votes.Contains(msg.Login)) return $"{msg.Name}, вы уже голосовали ({Votes.Count}/{need})";
        Votes.Add(msg.Login);
        if (Votes.Count >= need)
        {
            Skip("голосование", SkipBy.Vote);
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
        return new Track { Vid = "ym:" + y.Id, Title = y.Title, PageUrl = y.Url, Duration = y.Duration, Source = source, Artist = y.Artists, Cover = y.Cover };
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] : s;
    private static string Short(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;

    /// <summary>Найденный трек и, если она уже идёт, загрузка его звука (сведения и звук одним запуском yt-dlp).</summary>
    private sealed class Resolved
    {
        public Track Track;
        public YtJob Job;                 // загрузка, начатая сразу после поиска (может быть null)
        public bool Speculative;          // занят один из двух слотов опережающей загрузки
        public string QueryKey = "";      // запрос → ролик: запомнить, если заказ принят
        public SemaphoreSlim Slots;
        public void Release()
        {
            if (!Speculative) return;
            Speculative = false;
            Slots.Release();
        }
        /// <summary>Заказ отклонён: загрузка прерывается, недокачанный файл удаляется.</summary>
        public void Drop() { Job?.Cancel(); Release(); }
    }

    private static readonly Regex YtIdInUrl = new(@"(?:[?&]v=|youtu\.be/|/shorts/|/live/|/embed/)([A-Za-z0-9_-]{11})", RegexOptions.Compiled);

    private static string NormQuery(string q) => Regex.Replace((q ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    private void RememberQuery(string key, string vid)
    {
        if (key == "" || vid == "") return;
        if (!_qCache.ContainsKey(key)) _qOrder.AddLast(key);
        _qCache[key] = vid;
        while (_qOrder.Count > 500) { _qCache.Remove(_qOrder.First.Value); _qOrder.RemoveFirst(); }
    }

    private void RememberInfo(Track t)
    {
        if (t.Vid == "" || t.Vid.StartsWith("ym:")) return;
        _infoCache[t.Vid] = (t.Clone(), ActionLog.Now);
        if (_infoCache.Count > 500)
            foreach (var k in _infoCache.OrderBy(p => p.Value.Ts).Take(_infoCache.Count - 500).Select(p => p.Key).ToList()) _infoCache.Remove(k);
    }

    private Track CachedInfo(string vid) =>
        vid != "" && _infoCache.TryGetValue(vid, out var c) && ActionLog.Now - c.Ts < 6 * 3600 ? c.T.Clone() : null;

    /// <summary>
    /// Сведения о ролике YouTube и сразу его загрузка. Сведения из кэша (6 ч) — без обращения к YouTube;
    /// файл в кэше — без загрузки. Загрузка начинается до проверок; если заказ отклонят, её прервут.
    /// </summary>
    private async Task<Resolved> YtById(string vid, string url, string source, OrderTiming tm, Track flat = null)
    {
        var info = CachedInfo(vid);
        var hasFile = CachedFile(vid) != null;
        if (info != null)
        {
            info.Source = source;
            tm.Info = 0; tm.Cache += (tm.Cache == "" ? "" : ", ") + "сведения";
            if (hasFile) return new Resolved { Track = info };
        }
        // свободен слот опережающей загрузки — качаем звук тем же запуском, что и сведения
        var spec = !hasFile && _speculative.Wait(0);
        if (info != null && !spec) return new Resolved { Track = info };   // скачается обычным порядком, когда подойдёт очередь
        var job = _hub.YtDlp.Fetch(url, source, Config.CacheDir, download: spec);
        // сведения уже есть — ждать их от yt-dlp не нужно, нужен только звук
        if (info != null) return new Resolved { Track = info, Job = job, Speculative = true, Slots = _speculative };
        var sw = Stopwatch.StartNew();
        try
        {
            var got = await job.Info;
            if (info == null)
            {
                info = got;
                tm.Info = sw.Elapsed.TotalSeconds;
                tm.Launch = job.LaunchSec;
                if (flat != null && info.Views < 0) info.Views = flat.Views;
                RememberInfo(info);
            }
        }
        catch
        {
            job.Cancel();
            if (spec) _speculative.Release();
            throw;
        }
        return new Resolved { Track = info, Job = spec ? job : null, Speculative = spec, Slots = _speculative };
    }

    /// <summary>Проверки по данным быстрого поиска — до загрузки, чтобы не качать заведомо неподходящее. Тексты — как у полной проверки.</summary>
    private string PreCheck(Track c, long minViews)
    {
        var maxSec = Cfg.Int("max_duration_min", 8) * 60;
        if (c.Duration > maxSec) return $"трек длиннее {Cfg.Int("max_duration_min")} мин";
        if (Banned(c.Title) != null) return "запрещённое слово в названии трека";
        if (minViews > 0 && c.Views >= 0 && c.Views < minViews) return $"у видео меньше {YtDlp.FormatViews(minViews)} просмотров";
        if (TrackBlocked(c)) return "этот трек заблокирован";
        return BlockedBy(c);
    }

    private async Task<Resolved> YtSearchOrder(string q, string source, long minViews, OrderTiming tm, bool manual)
    {
        var key = NormQuery(q);
        // этот запрос уже заказывали — сразу к ролику, без поиска
        if (_qCache.TryGetValue(key, out var known))
        {
            var url = "https://www.youtube.com/watch?v=" + known;
            if (!manual)
            {
                var rep = RepeatReason(known, CachedInfo(known) is { } ci ? RepeatKeys(ci) : new HashSet<string>());
                if (rep != null) throw new ResolveException(rep);
            }
            try
            {
                tm.Search = 0; tm.Cache += (tm.Cache == "" ? "" : ", ") + "запрос";
                var r = await YtById(known, url, source, tm);
                r.QueryKey = key;
                return r;
            }
            catch (ResolveException) { _qCache.Remove(key); _qOrder.Remove(key); tm.Search = -1; }
        }
        var sw = Stopwatch.StartNew();
        var cands = await _hub.YtDlp.SearchFlat(q, source, minViews);
        tm.Search = sw.Elapsed.TotalSeconds;
        ResolveException last = null;
        foreach (var c in cands.Take(3))
        {
            if (!manual)
            {
                var pre = PreCheck(c, minViews) ?? RepeatReason(c);
                if (pre != null) throw new ResolveException(pre);
            }
            try
            {
                var r = await YtById(c.Vid, c.PageUrl, source, tm, c);
                r.QueryKey = key;
                return r;
            }
            catch (ResolveException e) when (YtDlp.CandidateError(e))
            {
                last = e;   // это видео недоступно (18+, удалено…) — загрузка прервана, берём следующее
            }
        }
        throw new ResolveException("подходящие видео недоступны: " + last?.Message);
    }

    private async Task<Resolved> SearchAny(string q, string source, long minViews, OrderTiming tm, bool manual)
    {
        var order = SearchOrder();
        if (order.Count == 0) throw new ResolveException("поиск по названию отключён, пришлите ссылку");
        var errors = new List<string>();
        foreach (var s in order)
        {
            try
            {
                if (s == "yandex")
                {
                    var sw = Stopwatch.StartNew();
                    var y = FromYandex(await _hub.Yandex.Search(q), source);
                    tm.Search = sw.Elapsed.TotalSeconds;
                    return new Resolved { Track = y };
                }
                return await YtSearchOrder(q, source, minViews, tm, manual);
            }
            catch (Exception e)   // пробуем следующий источник
            {
                // отказ по правилам (длительность, повтор…) — не ошибка источника, другой источник не нужен
                if (e is ResolveException re && s == "youtube" && IsRuleReject(re.Message)) throw;
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

    /// <summary>Отказ по правилам заказа (а не ошибка поиска): его возвращаем зрителю как есть.</summary>
    private static bool IsRuleReject(string m) =>
        IsRepeatText(m) || m.StartsWith("трек длиннее") || m.StartsWith("запрещённое слово") || m.StartsWith("у видео меньше")
        || m == "этот трек заблокирован" || m.EndsWith("в чёрном списке");

    /// <summary>Находит трек по ссылке или названию (для панели и проверок, без заказа).</summary>
    public async Task<Track> Resolve(string query, long minViews = 0)
    {
        var r = await ResolveOrder(query, minViews, new OrderTiming(), true);
        r.Drop();
        return r.Track;
    }

    private async Task<Resolved> ResolveOrder(string query, long minViews, OrderTiming tm, bool manual)
    {
        if (ResolveOverride != null) return new Resolved { Track = await ResolveOverride(query) };
        var q = query.Trim();
        var src = Cfg.Obj("sources");
        if (SpotifyRe.IsMatch(q))
        {
            if (!src.Bool("spotify")) throw new ResolveException("ссылки Spotify отключены");
            return await SearchAny(await SpotifyQuery(q), "spotify", minViews, tm, manual);
        }
        var ym = YandexMusic.TrackRe.Match(q);
        if (ym.Success)
        {
            if (!src.Bool("yandex")) throw new ResolveException("ссылки Яндекс Музыки отключены");
            try { return new Resolved { Track = FromYandex(await _hub.Yandex.Track(ym.Groups[1].Value), "yandex") }; }
            catch (ResolveException) { throw; }
            catch (Exception e) { throw new ResolveException($"Яндекс Музыка: {e.Message}"); }
        }
        if (YtRe.IsMatch(q))
        {
            if (!src.Bool("youtube")) throw new ResolveException("ссылки YouTube отключены");
            var m = YtIdInUrl.Match(q);
            var vid = m.Success ? m.Groups[1].Value : "";
            // повтор проверяем сразу по ID из ссылки — до обращения к YouTube
            if (!manual && vid != "")
            {
                var rep = RepeatReason(vid, CachedInfo(vid) is { } ci ? RepeatKeys(ci) : new HashSet<string>());
                if (rep != null) throw new ResolveException(rep);
            }
            return await YtById(vid, q, "youtube", tm);
        }
        if (UrlRe.IsMatch(q)) throw new ResolveException("поддерживаются ссылки YouTube, Яндекс Музыки, Spotify или название трека");
        return await SearchAny(q, "search", minViews, tm, manual);
    }

    private string Banned(string text)
    {
        var low = text.ToLowerInvariant();
        return Cfg.StrList("banned_words").FirstOrDefault(w => w != "" && low.Contains(w.ToLowerInvariant()));
    }

    private bool TrackBlocked(Track t)
    {
        var bt = Cfg.StrList("blocked_tracks").Select(b => b.ToLowerInvariant()).ToList();
        return bt.Contains(t.Vid.ToLowerInvariant()) || bt.Contains(t.Title.ToLowerInvariant()) || bt.Contains(t.PageUrl.ToLowerInvariant());
    }

    /// <summary>Проверки найденного трека — те же, что и раньше, плюс защита от повторов (последней).</summary>
    private string CheckTrack(Track track, long minViews)
    {
        var maxSec = Cfg.Int("max_duration_min", 8) * 60;
        if (track.Duration <= 0) return "не удалось определить длительность";
        if (track.Duration > maxSec) return $"трек длиннее {Cfg.Int("max_duration_min")} мин";
        if (Banned(track.Title) != null) return "запрещённое слово в названии трека";
        // Фильтр просмотров — только для YouTube (у Яндекс Музыки просмотров нет)
        if (minViews > 0 && !track.Vid.StartsWith("ym:") && track.Views >= 0 && track.Views < minViews)
            return $"у видео меньше {YtDlp.FormatViews(minViews)} просмотров";
        if (TrackBlocked(track)) return "этот трек заблокирован";
        return BlockedBy(track) ?? RepeatReason(track);
    }

    public async Task<(bool Ok, string Text, Track Track)> Request(string query, string name, string login,
        int priority = 0, bool manual = false, (string, string, int)? redemption = null,
        Task<int> priorityTask = null, OrderTiming timing = null)
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
        var tm = timing ?? new OrderTiming();
        Resolved r;
        try { r = await ResolveOrder(query, minViews, tm, manual); }
        catch (ResolveException e) { return (false, e.Message, null); }
        catch (Exception e) { return (false, $"не удалось найти трек: {YtDlp.Explain(e.Message)}", null); }
        var track = r.Track;
        if (!manual)
        {
            var why = CheckTrack(track, minViews);
            if (why != null) { r.Drop(); return (false, why, null); }
        }
        // приоритет (VIP, подписка) проверялся параллельно с поиском
        if (priorityTask != null)
        {
            try { priority = await priorityTask; } catch { priority = 0; }
        }
        // пока искали — этот же трек мог успеть заказать другой зритель
        if (!manual && RepeatReason(track) is { } rep2) { r.Drop(); return (false, rep2, null); }
        track.Requester = name;
        track.Login = login;
        track.Priority = priority;
        track.Redemption = redemption;
        track.Pay = redemption != null ? "pending" : "";   // баллы спишутся, когда трек заиграет
        track.Timing = tm;
        track.Job = r.Job;
        tm.Immediate = (Current == null || Current.Fallback) && !Queue.Any(q => !q.Fallback);
        Queue.RemoveAll(q => q.Fallback);   // заранее выбранный резервный трек уступает заказу
        var idx = Queue.FindIndex(x => x.Priority < priority);
        if (idx < 0) idx = Queue.Count;
        Queue.Insert(idx, track);
        if (r.Job != null) _downloads[track.Vid] = FromJob(track, r.Job);
        r.Release();
        RememberQuery(r.QueryKey, track.Vid.StartsWith("ym:") ? "" : track.Vid);
        // заказ важнее резервного плейлиста: фон доигрывает, пока заказ не скачан, затем плавный переход
        if (Current?.Fallback == true && Cfg.Bool("fallback_interrupt", true) && idx == 0)
            _ = InterruptFallbackWhenReady(track, Current);
        Wake();
        Prefetch();
        if (!manual) PlayChime();
        return (true, $"«{track.Title}» добавлен в очередь (позиция {idx + 1})", track);
    }

    // ---------- проверка скорости заказа ----------
    // Прогоняет набор заказов двумя способами — прежним (до 2.7.1) и новым — и печатает время каждого шага.
    // Файлы скачиваются во временную папку и удаляются; очередь, кэш и баллы не затрагиваются.
    private sealed class BenchPass { public double Search = -1, Info = -1, Download = -1, Loudness = -1, Total = -1; public string Error = ""; }
    private sealed class BenchRow { public string Query = ""; public BenchPass Old = new(), New = new(); }
    private sealed class BenchRun { public bool Running; public int Done, Total; public double Launch = -1; public string Text = "", Error = ""; public List<BenchRow> Rows = new(); }
    private BenchRun _bench;

    public object BenchState()
    {
        var b = _bench;
        if (b == null) return null;
        object P(BenchPass p) => new { search = R(p.Search), info = R(p.Info), download = R(p.Download), loudness = R(p.Loudness), total = R(p.Total), error = p.Error };
        return new
        {
            running = b.Running, done = b.Done, total = b.Total, launch = R(b.Launch), text = b.Text, error = b.Error,
            rows = b.Rows.Select(r => new { query = r.Query, old = P(r.Old), @new = P(r.New) }).ToList(),
        };
    }

    private static double R(double x) => x < 0 ? -1 : Math.Round(x, 2);

    public void StartBench(List<string> items)
    {
        if (_bench?.Running == true) throw new UserError("Проверка уже идёт");
        items = items.Select(x => x.Trim()).Where(x => x != "").Take(20).ToList();
        if (items.Count == 0) throw new UserError("Добавьте хотя бы один заказ: название или ссылку YouTube");
        _bench = new BenchRun { Running = true, Total = items.Count };
        _ = RunBench(items, _bench);
    }

    private async Task RunBench(List<string> items, BenchRun b)
    {
        var root = Path.Combine(Path.GetTempPath(), "Beg1nnnerOS-bench");
        try
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            b.Launch = await _hub.YtDlp.LaunchTime();
            for (var i = 0; i < items.Count; i++)
            {
                var row = new BenchRow { Query = items[i] };
                b.Rows.Add(row);
                // порядок способов чередуется, чтобы ни один не выигрывал за счёт «прогретого» YouTube
                foreach (var pass in i % 2 == 0 ? new[] { "new", "old" } : new[] { "old", "new" })
                {
                    var dir = Path.Combine(root, pass + i);
                    Directory.CreateDirectory(dir);
                    var p = pass == "old" ? row.Old : row.New;
                    try { await (pass == "old" ? BenchOld(items[i], dir, p) : BenchNew(items[i], dir, p)); }
                    catch (Exception e) { p.Error = Reason(e); }
                }
                b.Done++;
            }
            b.Text = BenchTable(b);
            foreach (var line in b.Text.Split('\n')) _hub.Log.Add("debug", line);
        }
        catch (Exception e) { b.Error = Reason(e); }
        finally
        {
            b.Running = false;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>Прежний способ: поиск → отдельный запуск за сведениями → отдельный запуск для скачивания → измерение громкости до старта.</summary>
    private async Task BenchOld(string q, string dir, BenchPass p)
    {
        var all = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        string url = q;
        if (!YtRe.IsMatch(q)) { var c = await _hub.YtDlp.SearchFlat(q, "search"); p.Search = sw.Elapsed.TotalSeconds; url = c[0].PageUrl; }
        else p.Search = 0;
        sw.Restart();
        var info = await _hub.YtDlp.ByUrl(url, "youtube");
        p.Info = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var path = await _hub.YtDlp.DownloadAudio(info.Vid, dir);
        p.Download = sw.Elapsed.TotalSeconds;
        sw.Restart();
        await Task.Run(() => Loudness.Measure(path));
        p.Loudness = sw.Elapsed.TotalSeconds;
        p.Total = all.Elapsed.TotalSeconds;
    }

    /// <summary>Новый способ: поиск → сведения и звук одним запуском → старт без ожидания громкости (её время — для справки).</summary>
    private async Task BenchNew(string q, string dir, BenchPass p)
    {
        var all = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        string url = q;
        if (!YtRe.IsMatch(q)) { var c = await _hub.YtDlp.SearchFlat(q, "search"); p.Search = sw.Elapsed.TotalSeconds; url = c[0].PageUrl; }
        else p.Search = 0;
        sw.Restart();
        var job = _hub.YtDlp.Fetch(url, "youtube", dir);
        await job.Info;
        p.Info = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var path = await job.File;
        p.Download = sw.Elapsed.TotalSeconds;
        p.Total = all.Elapsed.TotalSeconds;   // плеер стартует здесь — громкость меряется уже во время воспроизведения
        sw.Restart();
        await Task.Run(() => Loudness.Measure(path, maxSeconds: 25));
        p.Loudness = sw.Elapsed.TotalSeconds;
    }

    private static string BenchTable(BenchRun b)
    {
        string F(double x) => x < 0 ? "—" : x.ToString("0.0", Ru);
        var lines = new List<string>
        {
            $"Проверка скорости заказа: {b.Rows.Count} заказов · запуск yt-dlp (--version) {F(b.Launch)} с",
            "Заказ | было: поиск / сведения / загрузка / громкость = всего | стало: поиск / сведения+загрузка… / громкость (в фоне) = всего",
        };
        foreach (var r in b.Rows)
        {
            var o = r.Old.Error != "" ? "ошибка: " + r.Old.Error : $"{F(r.Old.Search)} / {F(r.Old.Info)} / {F(r.Old.Download)} / {F(r.Old.Loudness)} = {F(r.Old.Total)}";
            var n = r.New.Error != "" ? "ошибка: " + r.New.Error : $"{F(r.New.Search)} / {F(r.New.Info)} / {F(r.New.Download)} / ({F(r.New.Loudness)}) = {F(r.New.Total)}";
            lines.Add($"{Short(r.Query, 50)} | {o} | {n}");
        }
        var ok = b.Rows.Where(r => r.Old.Error == "" && r.New.Error == "").ToList();
        if (ok.Count > 0)
            lines.Add($"В среднем: было {F(ok.Average(r => r.Old.Total))} с, стало {F(ok.Average(r => r.New.Total))} с");
        return string.Join("\n", lines);
    }

    // ---------- чёрный список исполнителей и каналов ----------
    private static readonly Regex ArtistSplit = new(@"\s*(?:,|&|/|;|\bfeat\.?|\bft\.?|\bx\b|\bи\b)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleArtist = new(@"^(.+?)\s+[-–—]\s+.+$", RegexOptions.Compiled);

    /// <summary>Исполнители трека: из сведений источника и из названия вида «Исполнитель — Песня».</summary>
    public static List<string> ArtistsOf(Track t)
    {
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.Artist)) raw.Add(t.Artist);
        var m = TitleArtist.Match(t.Title ?? "");
        if (m.Success) raw.Add(m.Groups[1].Value);
        return raw.SelectMany(r => ArtistSplit.Split(r)).Select(a => a.Trim().Trim('"', '«', '»')).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly Regex YtId = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    /// <summary>Обложка трека: сохранённая (Яндекс) или превью видео YouTube (16:9, без чёрных полос).</summary>
    public static string CoverOf(Track t)
    {
        if (!string.IsNullOrEmpty(t.Cover)) return t.Cover;
        return YtId.IsMatch(t.Vid ?? "") ? $"https://i.ytimg.com/vi/{t.Vid}/mqdefault.jpg" : "";
    }

    private JsonArray CfgList(string k)
    {
        if (Cfg[k] is not JsonArray a) Cfg[k] = a = new JsonArray();
        return a;
    }

    private static string Norm(string s) => (s ?? "").Trim().TrimStart('@').ToLowerInvariant();

    /// <summary>Причина отказа, если исполнитель или канал трека в чёрном списке; иначе null.</summary>
    public string BlockedBy(Track t)
    {
        var artists = Cfg.StrList("blocked_artists").Select(Norm).Where(a => a != "").ToList();
        if (artists.Count > 0)
        {
            var have = ArtistsOf(t).Select(Norm).ToList();
            var title = " " + Regex.Replace(Norm(t.Title), @"[^\p{L}\p{N}]+", " ") + " ";
            foreach (var a in artists)
            {
                var words = " " + Regex.Replace(a, @"[^\p{L}\p{N}]+", " ").Trim() + " ";
                if (have.Contains(a) || (words.Trim().Length >= 3 && title.Contains(words)))
                    return "исполнитель в чёрном списке";
            }
        }
        var channels = Cfg.StrList("blocked_channels").Select(Norm).Where(c => c != "").ToList();
        if (channels.Count > 0 && (t.Channel != "" || t.ChannelId != ""))
        {
            var keys = new[] { Norm(t.Channel) }.Concat(t.ChannelId.Split(' ').Select(Norm)).Where(k => k != "").ToList();
            foreach (var c in channels)
            {
                // в списке может быть название, @псевдоним, ID канала или ссылка на него
                var key = c.Contains("youtube.com/") ? Norm(c.Split('/').Last(x => x != "")) : c;
                if (keys.Contains(key)) return "канал YouTube в чёрном списке";
            }
        }
        return null;
    }

    /// <summary>Добавляет исполнителя или канал текущего трека в чёрный список и пропускает трек.</summary>
    public string BlockCurrent(string what)
    {
        var cur = Current ?? throw new UserError("Сейчас ничего не играет");
        string item;
        if (what == "artist")
        {
            item = ArtistsOf(cur).FirstOrDefault() ?? throw new UserError("Не удалось определить исполнителя этого трека");
            CfgList("blocked_artists").Add(item);
        }
        else if (what == "channel")
        {
            if (cur.Channel == "") throw new UserError("У этого трека нет канала YouTube");
            item = cur.Channel;
            CfgList("blocked_channels").Add(item);
        }
        else
        {
            item = cur.Title;
            CfgList("blocked_tracks").Add(cur.Vid != "" ? cur.Vid : cur.Title);
        }
        _hub.Settings.Save();
        // заказы того же исполнителя или канала, уже стоящие в очереди, тоже убираем (баллы за них возвращаются)
        var removed = what == "track" ? new List<Track>() : Queue.Where(q => !q.Fallback && BlockedBy(q) != null).ToList();
        foreach (var q in removed) { Queue.Remove(q); ForgetDownload(q); }
        RefundMany(removed, "трек заблокирован стримером");
        Skip("заблокирован", SkipBy.Block);
        return $"«{item}» в чёрном списке" + (removed.Count > 0 ? $", из очереди убрано: {removed.Count}" : "");
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
                artist = string.Join(", ", ArtistsOf(cur)), channel = cur.Channel, cover = CoverOf(cur),
            };
        return new
        {
            current = curObj,
            queue = Queue.Select(t => t.Public()).ToList(),
            volume = Cfg.Int("volume", 60),
            votes = Votes.Count,
            votes_needed = Cfg.Int("skip_votes", 3),
            toast = new { id = ToastId, text = ToastText },
            notice = new { id = _noticeId, text = _noticeText },
            output = Cfg.Str("output", "app"),
            fallback_status = FallbackStatus,
            liked_count = LikedItems.Count,
            restored = ActionLog.Now - RestoredAt < 60 ? RestoredInfo : "",
            obs_connected = ObsConnected,
            pay = cur?.Pay ?? "",
            avg_start = AvgStart() is var a && a.Count > 0 ? new { sec = a.Avg, count = a.Count } : null,
            bench = BenchState(),
        };
    }
}
