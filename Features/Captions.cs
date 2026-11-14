using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;
using NAudio.Wave;
using Vosk;
using Whisper.net;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Субтитры речи стримера в OBS. Микрофон → распознавание на этом компьютере (Whisper или Vosk) →
/// источник /captions. Whisper умеет сразу переводить речь на английский. Настройки — раздел «captions».
/// </summary>
public sealed class Captions : IDisposable
{
    public sealed record ModelInfo(string Key, string File, string Title, int SizeMb, string Fallback);

    public static readonly ModelInfo[] WhisperModels =
    {
        new("tiny", "ggml-tiny-q5_1.bin", "Tiny · 32 МБ · для слабых ПК", 32, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny-q5_1.bin"),
        new("base", "ggml-base-q5_1.bin", "Base · 60 МБ · рекомендуем", 60, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base-q5_1.bin"),
        new("small", "ggml-small-q5_1.bin", "Small · 190 МБ · точнее, нужен мощный ПК", 190, "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin"),
    };
    public static readonly ModelInfo[] VoskModels =
    {
        new("ru", "vosk-model-small-ru-0.22.zip", "Русский · 45 МБ", 45, "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip"),
        new("en", "vosk-model-small-en-us-0.15.zip", "English · 40 МБ", 40, "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"),
    };
    private const string ModelsRelease = "https://github.com/beg1nnner/Beg1nnnerOS/releases/download/models/";

    // Whisper иногда «слышит» в тишине фразы из обучающих субтитров — их не показываем
    private static readonly Regex Hallucination = new(
        @"продолжение следует|субтитр|dimatorzok|редактор|корректор|спасибо за просмотр|подписывайтесь|thank you for watching|thanks for watching|please subscribe|^\W*$|^\s*\[.*\]\s*$|^\s*\(.*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Hub _hub;
    private readonly object _lock = new();
    private readonly List<(long Id, string Text, string En, double Ts)> _lines = new();
    private long _nextId = 1;
    private string _partial = "";
    private double _level;
    private bool _speaking;
    public string Status { get; private set; } = "выключены";
    public bool Running { get; private set; }

    // загрузка модели
    private string _dlKey = "";
    private double _dlProgress;
    private string _dlError = "";

    // захват и распознавание
    private WaveInEvent _wave;
    private CancellationTokenSource _cts;
    private BlockingCollection<short[]> _audio;
    private Task _worker;

    public Captions(Hub hub) => _hub = hub;
    private JsonObject Cfg => _hub.S.Obj("captions") ?? new JsonObject();
    public static string ModelsDir => Path.Combine(Config.Root, "models");

    // ---------- модели ----------
    private static ModelInfo FindModel(string engine, string key) =>
        (engine == "vosk" ? VoskModels : WhisperModels).FirstOrDefault(m => m.Key == key)
        ?? (engine == "vosk" ? VoskModels[0] : WhisperModels[1]);

    private static string ModelPath(ModelInfo m) =>
        m.File.EndsWith(".zip") ? Path.Combine(ModelsDir, m.File[..^4]) : Path.Combine(ModelsDir, m.File);

    public static bool HasModel(string engine, string key)
    {
        var p = ModelPath(FindModel(engine, key));
        return engine == "vosk" ? Directory.Exists(p) : File.Exists(p) && new FileInfo(p).Length > 1_000_000;
    }

    private (string Engine, ModelInfo Model) Current()
    {
        var c = Cfg;
        var engine = c.Str("engine", "whisper") == "vosk" ? "vosk" : "whisper";
        var key = engine == "vosk" ? (c.Str("lang", "ru") == "en" ? "en" : "ru") : c.Str("model", "base");
        return (engine, FindModel(engine, key));
    }

    /// <summary>Скачивает модель: сначала с GitHub (релиз «models»), если нет — с исходного сайта.</summary>
    public string StartDownload(string engine, string key)
    {
        if (_dlKey != "") throw new UserError("Модель уже скачивается");
        var m = FindModel(engine == "vosk" ? "vosk" : "whisper", key);
        if (HasModel(engine, m.Key)) return "Модель уже скачана";
        _dlKey = engine + ":" + m.Key; _dlProgress = 0; _dlError = "";
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(ModelsDir);
                var tmp = Path.Combine(ModelsDir, m.File + ".part");
                Exception last = null;
                foreach (var url in new[] { ModelsRelease + m.File, m.Fallback })
                {
                    try { await Download(url, tmp, m.SizeMb); last = null; break; }
                    catch (Exception e) { last = e; }
                }
                if (last != null) throw last;
                if (m.File.EndsWith(".zip"))
                {
                    var dir = ModelPath(m);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    ZipFile.ExtractToDirectory(tmp, ModelsDir, true);
                    File.Delete(tmp);
                    if (!Directory.Exists(dir)) throw new IOException("в архиве нет папки модели");
                }
                else File.Move(tmp, ModelPath(m), true);
                _hub.Loop.Post(() =>
                {
                    _hub.Log.Add("system", $"Субтитры: модель «{m.Title}» скачана");
                    Apply();
                });
            }
            catch (Exception e)
            {
                _dlError = e is HttpRequestException or TaskCanceledException
                    ? "не удалось скачать модель — проверьте интернет (GitHub или huggingface.co)"
                    : e.Message;
                _hub.Loop.Post(() => _hub.Log.Error("Субтитры: " + _dlError));
            }
            finally { _dlKey = ""; }
        });
        return "Скачиваю модель…";
    }

    private async Task Download(string url, string path, int sizeMb)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Beg1nnnerOS/{Config.Version}");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? sizeMb * 1024L * 1024;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(path);
        var buf = new byte[1 << 16];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            done += n;
            _dlProgress = Math.Min(1, done / (double)Math.Max(1, total));
        }
        if (done < total * 0.95) throw new IOException("файл скачался не полностью");
    }

    // ---------- включение / выключение ----------
    /// <summary>Применяет настройки: запускает или останавливает распознавание.</summary>
    public void Apply()
    {
        Stop();
        var c = Cfg;
        if (!c.Bool("enabled")) { Status = "выключены"; return; }
        var (engine, model) = Current();
        if (!HasModel(engine, model.Key)) { Status = "нужно скачать модель"; return; }
        try { Start(engine, model, c); }
        catch (Exception e)
        {
            Stop();
            Status = "ошибка: " + e.Message;
            _hub.Log.Error("Субтитры: " + e.Message);
        }
    }

    public bool Toggle()
    {
        var on = !Cfg.Bool("enabled");
        _hub.S.Set("captions.enabled", on);
        _hub.Settings.Save();
        Apply();
        _hub.Log.Add("system", on ? "Субтитры включены" : "Субтитры выключены");
        return on;
    }

    private void Start(string engine, ModelInfo model, JsonObject c)
    {
        _cts = new CancellationTokenSource();
        _audio = new BlockingCollection<short[]>(new ConcurrentQueue<short[]>(), 400);
        var lang = c.Str("lang", "ru") == "en" ? "en" : "ru";
        var show = c.Str("show", "both");
        var token = _cts.Token;
        var path = ModelPath(model);
        _worker = engine == "vosk"
            ? Task.Factory.StartNew(() => VoskLoop(path, lang, show, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : Task.Factory.StartNew(() => WhisperLoop(path, lang, show, token).GetAwaiter().GetResult(), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _wave = new WaveInEvent
        {
            DeviceNumber = DeviceIndex(c.Str("device")),
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 60,
        };
        _wave.DataAvailable += (s, e) =>
        {
            var samples = new short[e.BytesRecorded / 2];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, samples.Length * 2);
            double sum = 0;
            foreach (var x in samples) sum += (double)x * x;
            _level = samples.Length > 0 ? Math.Sqrt(sum / samples.Length) / 32768.0 : 0;
            if (!_audio.IsAddingCompleted) _audio.TryAdd(samples);
        };
        _wave.RecordingStopped += (s, e) =>
        {
            if (e.Exception != null) _hub.Loop.Post(() => { Status = "микрофон отключился: " + e.Exception.Message; Running = false; });
        };
        _wave.StartRecording();
        Running = true;
        Status = engine == "vosk" ? "слушаю микрофон (Vosk)" : "слушаю микрофон (Whisper)";
    }

    public void Stop()
    {
        Running = false;
        try { _wave?.StopRecording(); } catch { }
        try { _wave?.Dispose(); } catch { }
        _wave = null;
        try { _audio?.CompleteAdding(); } catch { }
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        _level = 0; _speaking = false;
        lock (_lock) _partial = "";
    }

    public void Dispose() => Stop();

    public static List<object> Devices()
    {
        var list = new List<object>();
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                list.Add(new { id = WaveInEvent.GetCapabilities(i).ProductName, index = i });
        }
        catch { }
        return list;
    }

    private static int DeviceIndex(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
                if (WaveInEvent.GetCapabilities(i).ProductName == name) return i;
        }
        catch { }
        return 0;
    }

    // ---------- Whisper: фразы по паузам в речи ----------
    private async Task WhisperLoop(string modelPath, string lang, string show, CancellationToken ct)
    {
        try
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            var threads = Math.Clamp(Cfg.Int("threads", 0) > 0 ? Cfg.Int("threads") : Environment.ProcessorCount / 2, 1, 16);
            WhisperProcessor transcribe = null, translate = null;
            if (show != "en" || lang == "en")
                transcribe = factory.CreateBuilder().WithLanguage(lang).WithThreads(threads).WithNoContext().Build();
            if (show != "orig" && lang != "en")
                translate = factory.CreateBuilder().WithLanguage(lang).WithTranslate().WithThreads(threads).WithNoContext().Build();
            try
            {
                var phrase = new List<short>();
                var pre = new Queue<short[]>();   // ~300 мс до начала речи, чтобы не обрезать первое слово
                double noise = 0.006;
                var silenceMs = 0;
                foreach (var chunk in _audio.GetConsumingEnumerable(ct))
                {
                    var rms = Rms(chunk);
                    var ms = chunk.Length / 16;
                    var threshold = Math.Max(0.012, noise * 3);
                    var loud = rms > threshold;
                    if (!loud) noise = noise * 0.97 + rms * 0.03;
                    if (!_speaking)
                    {
                        pre.Enqueue(chunk);
                        while (pre.Count > 5) pre.Dequeue();
                        if (!loud) continue;
                        _speaking = true;
                        silenceMs = 0;
                        phrase.Clear();
                        foreach (var p in pre) phrase.AddRange(p);
                        pre.Clear();
                        SetPartial("…");
                        continue;
                    }
                    phrase.AddRange(chunk);
                    silenceMs = loud ? 0 : silenceMs + ms;
                    var seconds = phrase.Count / 16000.0;
                    if (silenceMs < 650 && seconds < 9) continue;
                    _speaking = false;
                    SetPartial("");
                    if (seconds < 0.5) { phrase.Clear(); continue; }
                    var samples = phrase.Select(x => x / 32768f).ToArray();
                    phrase.Clear();
                    var text = transcribe != null ? await Run(transcribe, samples, ct) : "";
                    var en = translate != null ? await Run(translate, samples, ct) : (lang == "en" ? text : "");
                    if (lang == "en") { en = text; text = show == "en" ? "" : text; }
                    AddLine(text, en);
                }
            }
            finally
            {
                transcribe?.Dispose();
                translate?.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _hub.Loop.Post(() => { Status = "ошибка распознавания: " + e.Message; Running = false; _hub.Log.Error("Субтитры (Whisper): " + e.Message); });
        }
    }

    private static async Task<string> Run(WhisperProcessor p, float[] samples, CancellationToken ct)
    {
        var parts = new List<string>();
        await foreach (var seg in p.ProcessAsync(samples, ct))
        {
            var t = (seg.Text ?? "").Trim();
            if (t != "" && !Hallucination.IsMatch(t)) parts.Add(t);
        }
        return string.Join(" ", parts).Trim();
    }

    // ---------- Vosk: слова появляются сразу ----------
    private void VoskLoop(string modelPath, string lang, string show, CancellationToken ct)
    {
        try
        {
            Vosk.Vosk.SetLogLevel(-1);
            using var model = new Model(modelPath);
            using var rec = new VoskRecognizer(model, 16000f);
            foreach (var chunk in _audio.GetConsumingEnumerable(ct))
            {
                var bytes = new byte[chunk.Length * 2];
                Buffer.BlockCopy(chunk, 0, bytes, 0, bytes.Length);
                _speaking = Rms(chunk) > 0.012;
                if (rec.AcceptWaveform(bytes, bytes.Length))
                {
                    var text = Cap(JsonNode.Parse(rec.Result())?.Str("text") ?? "");
                    SetPartial("");
                    if (text == "") continue;
                    if (show == "orig" || lang == "en") { AddLine(lang == "en" && show == "en" ? "" : text, lang == "en" ? text : ""); continue; }
                    // перевод — через локальный переводчик программы (если он настроен)
                    var t = text;
                    _hub.Loop.Post(() => _hub.Loop.Start(async () =>
                    {
                        string en = "";
                        try { en = (await _hub.Translator.Translate(t, lang, "en")).Text; } catch { }
                        AddLine(show == "en" ? "" : t, en);
                    }));
                }
                else SetPartial(Cap(JsonNode.Parse(rec.PartialResult())?.Str("partial") ?? ""));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _hub.Loop.Post(() => { Status = "ошибка распознавания: " + e.Message; Running = false; _hub.Log.Error("Субтитры (Vosk): " + e.Message); });
        }
    }

    // ---------- вывод ----------
    private static double Rms(short[] s)
    {
        if (s.Length == 0) return 0;
        double sum = 0;
        foreach (var x in s) sum += (double)x * x;
        return Math.Sqrt(sum / s.Length) / 32768.0;
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? "" : char.ToUpper(s[0]) + s[1..];

    private void SetPartial(string p) { lock (_lock) _partial = Censor(p); }

    private void AddLine(string text, string en)
    {
        text = Censor(text?.Trim() ?? ""); en = Censor(en?.Trim() ?? "");
        if (text == "" && en == "") return;
        lock (_lock)
        {
            _lines.Add((_nextId++, text, en, ActionLog.Now));
            if (_lines.Count > 10) _lines.RemoveRange(0, _lines.Count - 10);
        }
    }

    /// <summary>Слова из «Модерация → Запрещённые слова» заменяются звёздочками.</summary>
    private string Censor(string s)
    {
        if (string.IsNullOrEmpty(s) || !Cfg.Bool("censor", true)) return s;
        foreach (var w in _hub.S.StrList("mod.words.list"))
        {
            var word = w.TrimStart('=').Trim();
            if (word.Length < 2) continue;
            s = Regex.Replace(s, @"\b\w*" + Regex.Escape(word) + @"\w*\b", m => m.Value[0] + new string('*', Math.Max(1, m.Value.Length - 1)),
                RegexOptions.IgnoreCase);
        }
        return s;
    }

    public object State(bool full)
    {
        var c = Cfg;
        List<object> lines;
        string partial;
        lock (_lock)
        {
            lines = _lines.Select(l => (object)new { id = l.Id, text = l.Text, en = l.En, ts = l.Ts }).ToList();
            partial = _partial;
        }
        var (engine, model) = Current();
        var style = new
        {
            style = c.Str("style", "classic"), font_size = c.Int("font_size", 30), position = c.Str("position", "bottom"),
            max_lines = Math.Clamp(c.Int("max_lines", 2), 1, 4), hide_sec = Math.Clamp(c.Int("hide_sec", 5), 1, 60),
            show = c.Str("show", "both"),
        };
        if (!full) return new { enabled = c.Bool("enabled") && Running, lines, partial, speaking = _speaking, now = ActionLog.Now, cfg = style };
        return new
        {
            enabled = c.Bool("enabled"), running = Running, status = Status, lines, partial, speaking = _speaking,
            level = Math.Round(Math.Min(1, _level * 6), 3), now = ActionLog.Now, cfg = style,
            engine, model = model.Key, has_model = HasModel(engine, model.Key),
            download = new { active = _dlKey != "", key = _dlKey, progress = Math.Round(_dlProgress, 3), error = _dlError },
            whisper_models = WhisperModels.Select(m => new { key = m.Key, title = m.Title, ready = HasModel("whisper", m.Key) }),
            vosk_models = VoskModels.Select(m => new { key = m.Key, title = m.Title, ready = HasModel("vosk", m.Key) }),
        };
    }
}
