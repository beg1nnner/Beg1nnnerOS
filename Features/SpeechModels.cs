using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Модели распознавания речи — одни на субтитры и голосовые команды: одна папка, один список, одна загрузка.
///
/// Главное правило: программа никогда не скачивает модель сама. Загрузка начинается только из <see cref="Download"/>,
/// который вызывается по кнопке «Скачать» в панели. При запуске программы недокачанная загрузка не продолжается —
/// панель показывает «Загрузка не завершена» и кнопку «Продолжить».
///
/// Надёжность: докачка с места обрыва (Range, файл .part), проверка места на диске до начала, проверка размера,
/// формата и (если опубликована) контрольной суммы после, три повторные попытки, распаковка Vosk во временную папку
/// с переименованием в конце. После загрузки в models/manifest.json записывается SHA-256 — при запуске программы
/// модели сверяются с ним, повреждённая показывается как «нужно скачать заново».
/// </summary>
public sealed class SpeechModels
{
    public sealed record Model(string Engine, string Key, string File, string Name, string Hint, int SizeMb, int DiskMb, string Fallback)
    {
        public string Id => Engine + ":" + Key;
        public bool IsZip => File.EndsWith(".zip");
    }

    /// <summary>Список моделей. Адреса: сначала релиз «models» на GitHub, затем исходный сайт (Fallback).</summary>
    public static readonly Model[] All =
    {
        new("whisper", "tiny", "ggml-tiny-q5_1.bin", "Whisper Tiny", "для слабых ПК", 32, 32, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny-q5_1.bin"),
        new("whisper", "base", "ggml-base-q5_1.bin", "Whisper Base", "рекомендуем для субтитров", 60, 60, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin"),
        new("whisper", "small", "ggml-small-q5_1.bin", "Whisper Small", "точнее, нужен мощный ПК", 190, 190, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin"),
        new("vosk", "ru", "vosk-model-small-ru-0.22.zip", "Vosk · русский", "быстрая, для голосовых команд", 45, 90, "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip"),
        new("vosk", "en", "vosk-model-small-en-us-0.15.zip", "Vosk · English", "быстрая, для голосовых команд", 40, 70, "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"),
    };
    private const string ModelsRelease = "https://github.com/beg1nnner/Beg1nnnerOS/releases/download/models/";
    private static readonly int[] RetryPauseSec = { 3, 8, 15 };

    private readonly Hub _hub;
    private readonly object _lock = new();
    private readonly Queue<Model> _queue = new();
    private Model _active;
    private long _done, _total;
    private double _speed;                 // байт/с, сглаженная
    private string _phase = "";            // download | verify | unpack
    private string _error = "", _errorIds = "";
    private int _attempt;
    private CancellationTokenSource _cts;
    private readonly HashSet<string> _corrupt = new();
    private bool _checked;
    private long _seq;                     // растёт после каждой успешной загрузки — панель показывает тост
    private string _lastDone = "";
    private long _needSeq;                 // кто-то пытался включить функцию без модели — панель покажет подсказку

    /// <summary>Попытка включить субтитры или голосовые команды без модели (горячая клавиша, док-панель, Ctrl+K).</summary>
    public void NeedModel() { lock (_lock) _needSeq++; }

    public SpeechModels(Hub hub) => _hub = hub;

    public static string Dir => Path.Combine(Config.Root, "models");
    private static string ManifestPath => Path.Combine(Dir, "manifest.json");
    public static Model Find(string id) => All.FirstOrDefault(m => m.Id == id);
    public static Model Find(string engine, string key) => All.FirstOrDefault(m => m.Engine == engine && m.Key == key);
    public static string PathOf(Model m) => m.IsZip ? Path.Combine(Dir, m.File[..^4]) : Path.Combine(Dir, m.File);
    private static string PartOf(Model m) => Path.Combine(Dir, m.File + ".part");

    /// <summary>Модель на месте и не помечена как повреждённая.</summary>
    public bool Ready(Model m) => m != null && Present(m) && !_corrupt.Contains(m.Id) && _active?.Id != m.Id;

    private static bool Present(Model m)
    {
        var p = PathOf(m);
        return m.IsZip ? File.Exists(Path.Combine(p, "am", "final.mdl")) : File.Exists(p) && new FileInfo(p).Length > 1_000_000;
    }

    // ---------- какую модель брать ----------
    private string UiLang => _hub.S.Str("ui.language", "ru") == "en" ? "en" : "ru";
    private string SpeechLang => _hub.S.Str("captions.lang", "ru") == "en" ? "en" : "ru";

    /// <summary>Рекомендуемые: Whisper Base — для субтитров, маленькая Vosk под язык интерфейса — для голосовых команд.</summary>
    public Model RecommendedCaptions => Find("whisper", "base");
    public Model RecommendedVoice => Find("vosk", UiLang);

    /// <summary>Модель для субтитров: выбранная в «Дополнительно», а если её нет — любая скачанная подходящая.</summary>
    public Model ForCaptions()
    {
        var c = _hub.S.Obj("captions") ?? new JsonObject();
        var engine = c.Str("engine", "whisper") == "vosk" ? "vosk" : "whisper";
        var chosen = engine == "vosk" ? Find("vosk", SpeechLang) : Find("whisper", c.Str("model", "base"));
        if (Ready(chosen)) return chosen;
        foreach (var k in new[] { "base", "small", "tiny" })
            if (Ready(Find("whisper", k))) return Find("whisper", k);
        return Ready(Find("vosk", SpeechLang)) ? Find("vosk", SpeechLang) : null;
    }

    /// <summary>Модель для голосовых команд (когда субтитры выключены): быстрая Vosk, иначе любая скачанная Whisper.</summary>
    public Model ForVoice()
    {
        foreach (var m in new[] { Find("vosk", SpeechLang), Find("vosk", UiLang) })
            if (Ready(m)) return m;
        foreach (var k in new[] { "base", "tiny", "small" })
            if (Ready(Find("whisper", k))) return Find("whisper", k);
        return null;
    }

    // ---------- загрузка: только по кнопке ----------
    /// <summary>Ставит модели в очередь загрузки. Вызывается только из панели по нажатию «Скачать» / «Продолжить» / «Повторить».</summary>
    public string Download(IEnumerable<string> ids)
    {
        var list = ids.Select(Find).Where(m => m != null).Distinct().ToList();
        if (list.Count == 0) throw new UserError("Не выбрана модель");
        lock (_lock)
        {
            foreach (var m in list)
                if (!Ready(m) && _active?.Id != m.Id && !_queue.Any(q => q.Id == m.Id)) _queue.Enqueue(m);
            if (_queue.Count == 0 && _active == null) return "Модель уже скачана";
            _error = ""; _errorIds = "";
            if (_active == null) StartNext();
        }
        return "Скачиваю модель…";
    }

    /// <summary>«Отменить»: останавливает загрузку и очередь, недокачанный файл удаляется.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _queue.Clear();
            _cancelled = true;
            _cts?.Cancel();
        }
    }
    private bool _cancelled;

    private void StartNext()   // под _lock
    {
        if (_queue.Count == 0) { _active = null; return; }
        var m = _queue.Dequeue();
        _active = m; _done = 0; _total = m.SizeMb * 1024L * 1024; _speed = 0; _attempt = 0; _phase = "download"; _cancelled = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => Run(m, token));
    }

    private async Task Run(Model m, CancellationToken token)
    {
        string err = null;
        try
        {
            Directory.CreateDirectory(Dir);
            CheckSpace(m);
            Exception last = null;
            for (var attempt = 0; attempt <= RetryPauseSec.Length; attempt++)
            {
                _attempt = attempt;
                if (attempt > 0) await Task.Delay(RetryPauseSec[attempt - 1] * 1000, token);
                foreach (var url in new[] { ModelsRelease + m.File, m.Fallback })
                {
                    try { await Fetch(url, PartOf(m), token); last = null; break; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception e) { last = e; }
                }
                if (last == null) break;
            }
            if (last != null) throw last;
            _phase = "verify";
            await Verify(m, token);
            Install(m, token);
            _corrupt.Remove(m.Id);
            lock (_lock) { _seq++; _lastDone = m.Id; }
            _hub.Loop.Post(() =>
            {
                _hub.Log.Add("system", $"Модель распознавания «{m.Name}» скачана");
                _hub.Captions.Apply();   // если функция уже включена и ждала модель — заработает; сама ничего не включает
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (_cancelled) TryDelete(PartOf(m));
            _hub.Loop.Post(() => _hub.Log.Add("system", $"Загрузка модели «{m.Name}» отменена"));
        }
        catch (Exception e)
        {
            err = e switch
            {
                UserError ue => ue.Message,
                InvalidDataException => "скачанный файл повреждён — нажмите «Повторить», он скачается заново",
                HttpRequestException or TaskCanceledException or IOException =>
                    "не удалось скачать модель — нет связи с GitHub и запасным сайтом. Проверьте интернет и нажмите «Повторить»",
                _ => e.Message,
            };
            if (e is InvalidDataException) TryDelete(PartOf(m));
            _hub.Loop.Post(() => _hub.Log.Error($"Модель «{m.Name}»: {err}"));
        }
        finally
        {
            lock (_lock)
            {
                if (err != null) { _error = err; _errorIds = string.Join(",", new[] { m.Id }.Concat(_queue.Select(q => q.Id))); _queue.Clear(); }
                _active = null; _phase = "";
                StartNext();
            }
        }
    }

    private void CheckSpace(Model m)
    {
        var have = File.Exists(PartOf(m)) ? new FileInfo(PartOf(m)).Length : 0;
        var need = m.SizeMb * 1024L * 1024 - have + (m.IsZip ? m.DiskMb * 1024L * 1024 : 0) + 50L * 1024 * 1024;
        var free = FreeBytes();
        if (free >= 0 && free < need)
            throw new UserError($"мало места на диске: нужно ещё {Mb(need)} МБ, свободно {Mb(free)} МБ");
    }

    /// <summary>Скачивание с докачкой: если есть .part — просим продолжение (Range).</summary>
    private async Task Fetch(string url, string part, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Beg1nnnerOS/{Config.Version}");
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(30));   // соединение и заголовки
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        cts.CancelAfter(Timeout.Infinite);   // дальше обрыв ловит проверка «нет данных 30 секунд»
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
        {
            _done = _total = have;   // уже всё скачано
            return;
        }
        resp.EnsureSuccessStatusCode();
        var resume = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resume) have = 0;
        var total = resp.Content.Headers.ContentRange?.Length ?? (resp.Content.Headers.ContentLength is long len ? len + have : -1);
        _total = total > 0 ? total : _total;
        _done = have;
        await using var src = await resp.Content.ReadAsStreamAsync(token);
        await using (var dst = new FileStream(part, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new byte[1 << 16];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastBytes = _done; var lastT = 0.0;
            while (true)
            {
                // нет данных 30 секунд — обрыв, попробуем ещё раз с места остановки
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                int n;
                try { n = await src.ReadAsync(buf, idle.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("соединение оборвалось"); }
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), token);
                _done += n;
                var t = sw.Elapsed.TotalSeconds;
                if (t - lastT >= 0.5)
                {
                    var inst = (_done - lastBytes) / (t - lastT);
                    _speed = _speed <= 0 ? inst : _speed * 0.7 + inst * 0.3;
                    lastBytes = _done; lastT = t;
                }
            }
        }
        if (total > 0 && new FileInfo(part).Length != total)
            throw new IOException("файл скачался не полностью");
    }

    /// <summary>Проверка скачанного файла: размер, формат и контрольная сумма (если она опубликована рядом с моделью).</summary>
    private async Task Verify(Model m, CancellationToken token)
    {
        var part = PartOf(m);
        var len = new FileInfo(part).Length;
        if (len < m.SizeMb * 1024L * 1024 * 0.8) throw new InvalidDataException("размер не совпадает");
        if (!m.IsZip)
        {
            var head = new byte[4];
            await using (var f = File.OpenRead(part)) await f.ReadExactlyAsync(head, token);
            var magic = BitConverter.ToUInt32(head, 0);
            if (magic != 0x67676d6c && magic != 0x6c6d6767 && magic != 0x67676a74 && magic != 0x67676d66) throw new InvalidDataException("не модель Whisper");
        }
        var sha = await Sha256File(part, token);
        var expected = await PublishedSha(m, token);
        if (expected != "" && !string.Equals(expected, sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("контрольная сумма не совпала");
    }

    /// <summary>Контрольная сумма из релиза «models» (файл «имя.sha256»), если она там есть; иначе пусто.</summary>
    private static async Task<string> PublishedSha(Model m, CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Beg1nnnerOS/{Config.Version}");
            using var resp = await http.GetAsync(ModelsRelease + m.File + ".sha256", token);
            if (!resp.IsSuccessStatusCode) return "";
            var text = (await resp.Content.ReadAsStringAsync(token)).Trim();
            var hash = text.Split(' ', '\t', '\n')[0];
            return hash.Length == 64 ? hash : "";
        }
        catch (Exception) when (!token.IsCancellationRequested) { return ""; }
    }

    private void Install(Model m, CancellationToken token)
    {
        var part = PartOf(m);
        var target = PathOf(m);
        if (m.IsZip)
        {
            // распаковка во временную папку и переименование в конце: обрыв не оставит полупустую модель
            _phase = "unpack";
            var tmp = Path.Combine(Dir, ".unpack-" + m.Key);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(part, tmp, true);   // при ошибке CRC — InvalidDataException
            token.ThrowIfCancellationRequested();
            var inner = Path.Combine(tmp, Path.GetFileName(target));
            var src = Directory.Exists(inner) ? inner : tmp;
            if (!File.Exists(Path.Combine(src, "am", "final.mdl"))) { Directory.Delete(tmp, true); throw new InvalidDataException("в архиве нет модели"); }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(src, target);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            File.Delete(part);
        }
        else File.Move(part, target, true);
        WriteManifest(m);
    }

    // ---------- целостность ----------
    private JsonObject ReadManifest()
    {
        try { return File.Exists(ManifestPath) ? JsonNode.Parse(File.ReadAllText(ManifestPath)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private void WriteManifest(Model m)
    {
        lock (_lock)
        {
            var man = ReadManifest();
            man[m.Id] = new JsonObject { ["bytes"] = SizeOnDisk(m), ["sha256"] = Sha256Model(m), ["ts"] = ActionLog.Now };
            File.WriteAllText(ManifestPath, man.ToJsonString());
        }
    }

    /// <summary>
    /// При запуске программы: сверяем скачанные модели с manifest.json. Повреждённая помечается «нужно скачать заново» —
    /// сами ничего не скачиваем. Модели, скачанные прежними версиями (без записи в манифесте), проверяем по составу и добавляем.
    /// </summary>
    public void CheckOnStartup() => Task.Run(() =>
    {
        try
        {
            var man = ReadManifest();
            foreach (var m in All)
            {
                if (!Present(m)) continue;
                var rec = man[m.Id] as JsonObject;
                if (rec == null) { WriteManifest(m); continue; }
                if ((long)rec.Num("bytes") != SizeOnDisk(m) || !string.Equals(rec.Str("sha256"), Sha256Model(m), StringComparison.OrdinalIgnoreCase))
                {
                    lock (_lock) _corrupt.Add(m.Id);
                    _hub.Loop.Post(() => _hub.Log.Error($"Модель «{m.Name}» повреждена — скачайте её заново в «Субтитрах»"));
                }
            }
            // обломки прерванной распаковки
            foreach (var d in Directory.Exists(Dir) ? Directory.GetDirectories(Dir, ".unpack-*") : Array.Empty<string>()) TryDelete(d);
        }
        catch (Exception e) { _hub.Loop.Post(() => _hub.Log.Error("Проверка моделей распознавания: " + e.Message)); }
        finally { _checked = true; _hub.Loop.Post(() => _hub.Captions.Apply()); }
    });

    private static long SizeOnDisk(Model m)
    {
        var p = PathOf(m);
        return m.IsZip ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : new FileInfo(p).Length;
    }

    private static string Sha256Model(Model m)
    {
        var p = PathOf(m);
        if (!m.IsZip) return Sha256File(p, CancellationToken.None).GetAwaiter().GetResult();
        using var sha = SHA256.Create();
        foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(p, f).Replace('\\', '/'));
            sha.TransformBlock(name, 0, name.Length, null, 0);
            var data = File.ReadAllBytes(f);
            sha.TransformBlock(data, 0, data.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static async Task<string> Sha256File(string path, CancellationToken token)
    {
        await using var f = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(f, token)).ToLowerInvariant();
    }

    // ---------- удаление ----------
    /// <summary>Удаляет модель. Если она сейчас используется — сначала выключает функцию и сообщает об этом.</summary>
    public string Delete(string id)
    {
        var m = Find(id) ?? throw new UserError("Неизвестная модель");
        if (_active?.Id == id) throw new UserError("Модель сейчас скачивается — сначала отмените загрузку");
        var note = "";
        if (_hub.Captions.Running && _hub.Captions.CurrentModel?.Id == id)
        {
            var off = new List<string>();
            if (_hub.S.Bool("captions.enabled")) { _hub.S.Set("captions.enabled", false); off.Add("субтитры"); }
            if (_hub.S.Bool("captions.voice.enabled")) { _hub.S.Set("captions.voice.enabled", false); off.Add("голосовые команды"); }
            _hub.Settings.Save();
            _hub.Captions.Apply();
            if (off.Count > 0) note = (off.Count == 2 ? "Субтитры и голосовые команды выключены" : off[0] == "субтитры" ? "Субтитры выключены" : "Голосовые команды выключены")
                                      + " — они использовали эту модель. ";
        }
        _hub.Captions.Stop();
        var freed = Present(m) ? SizeOnDisk(m) : 0;
        TryDelete(PathOf(m));
        TryDelete(PartOf(m));
        lock (_lock)
        {
            _corrupt.Remove(m.Id);
            var man = ReadManifest(); man.Remove(m.Id);
            try { File.WriteAllText(ManifestPath, man.ToJsonString()); } catch { }
        }
        _hub.Captions.Apply();
        _hub.Log.Add("system", $"Модель «{m.Name}» удалена");
        return $"{note}Модель «{m.Name}» удалена, освобождено {Mb(freed)} МБ";
    }

    private static void TryDelete(string p)
    {
        try
        {
            if (Directory.Exists(p)) Directory.Delete(p, true);
            else if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }

    // ---------- состояние для панели ----------
    public static long FreeBytes()
    {
        try { Directory.CreateDirectory(Dir); return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Dir))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    private static long Mb(long bytes) => (long)Math.Ceiling(bytes / 1024.0 / 1024);

    private string StatusOf(Model m)
    {
        if (_active?.Id == m.Id) return "downloading";
        if (_queue.Any(q => q.Id == m.Id)) return "queued";
        if (Present(m)) return _corrupt.Contains(m.Id) ? "corrupt" : "ready";
        if (File.Exists(PartOf(m))) return "partial";
        if (_errorIds.Split(',').Contains(m.Id)) return "error";
        return "missing";
    }

    /// <summary>Коротко для /state: индикатор в шапке и тост «Модель скачана».</summary>
    public object Brief()
    {
        lock (_lock)
            return new
            {
                active = _active != null, name = _active?.Name ?? "",
                pct = _active != null && _total > 0 ? Math.Round(Math.Min(1, _done / (double)_total), 3) : 0,
                seq = _seq, done = _lastDone == "" ? "" : Find(_lastDone)!.Name, error = _error, need = _needSeq,
            };
    }

    public object State()
    {
        lock (_lock)
        {
            var cap = ForCaptions();
            var voice = ForVoice();
            var inUse = _hub.Captions.Running ? _hub.Captions.CurrentModel?.Id : null;
            return new
            {
                models = All.Select(m => new
                {
                    id = m.Id, engine = m.Engine, key = m.Key, name = m.Name, hint = m.Hint, size_mb = m.SizeMb, disk_mb = m.DiskMb,
                    status = StatusOf(m), part_mb = File.Exists(PartOf(m)) ? Mb(new FileInfo(PartOf(m)).Length) : 0,
                    in_use = m.Id == inUse,
                }),
                free_mb = FreeBytes() < 0 ? -1 : Mb(FreeBytes()),
                recommended = new { captions = RecommendedCaptions.Id, voice = RecommendedVoice.Id },
                ready = new { captions = cap != null, voice = voice != null },
                captions_model = cap?.Id ?? "", voice_model = voice?.Id ?? "", checked_ok = _checked,
                download = new
                {
                    active = _active != null, id = _active?.Id ?? "", name = _active?.Name ?? "", phase = _phase, attempt = _attempt,
                    done_mb = Math.Round(_done / 1048576.0, 1), total_mb = Math.Round(_total / 1048576.0, 1),
                    speed_kb = (long)(_speed / 1024), queue = _queue.Select(q => q.Id).ToArray(),
                    error = _error, error_ids = _errorIds == "" ? Array.Empty<string>() : _errorIds.Split(','),
                },
                seq = _seq,
            };
        }
    }
}
