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
using Whisper.net.LibraryLoader;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Субтитры речи стримера в OBS. Микрофон → распознавание на этом компьютере (Whisper или Vosk) →
/// источник /captions. Whisper умеет сразу переводить речь на английский. Настройки — раздел «captions».
/// </summary>
public sealed class Captions : IDisposable
{
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

    /// <summary>Модель, на которой сейчас работает распознавание (общая для субтитров и голосовых команд).</summary>
    public SpeechModels.Model CurrentModel { get; private set; }

    // захват и распознавание
    private WaveInEvent _wave;
    private CancellationTokenSource _cts;
    private BlockingCollection<short[]> _audio;
    private Task _worker;

    // ускорение: библиотека Whisper выбирается один раз за запуск программы
    private static bool? _runtimeGpu;
    private string _accel = "";
    private double _rtf;   // во сколько раз быстрее реального времени

    public Captions(Hub hub) => _hub = hub;

    /// <summary>Порядок библиотек Whisper: видеокарта (Vulkan — NVIDIA, AMD, Intel), если не вышло — процессор.</summary>
    private static void PrepareRuntime(bool gpu)
    {
        if (_runtimeGpu.HasValue) return;
        _runtimeGpu = gpu;
        RuntimeOptions.RuntimeLibraryOrder = gpu
            ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
            : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
    }

    private static string AccelName(RuntimeLibrary? lib) => lib switch
    {
        RuntimeLibrary.Vulkan => "видеокарта (Vulkan)",
        RuntimeLibrary.Cuda => "видеокарта (CUDA)",
        RuntimeLibrary.CpuNoAvx => "процессор (без AVX — медленно)",
        RuntimeLibrary.Cpu => "процессор",
        _ => "",
    };
    private JsonObject Cfg => _hub.S.Obj("captions") ?? new JsonObject();
    // Модели распознавания (список, папка, загрузка только по кнопке) — в SpeechModels, общие с голосовыми командами.

    // ---------- включение / выключение ----------
    /// <summary>Применяет настройки: запускает или останавливает распознавание.</summary>
    public void Apply()
    {
        var c = Cfg;
        var voice = _hub.Voice.Wants;
        // субтитры включены — модель субтитров (на ней же слушаются голосовые команды); только команды — быстрая модель для них
        var model = c.Bool("enabled") ? _hub.Models.ForCaptions() : voice ? _hub.Models.ForVoice() : null;
        var engine = model?.Engine ?? "";
        // распознаватель один на субтитры и голосовые команды; перезапускаем, только если изменилось что-то для него важное
        var sig = string.Join("|", c.Bool("enabled") || voice, c.Str("device"), model?.Id, c.Str("lang"), c.Str("show"), c.Bool("gpu", true), c.Int("threads"), voice && c.Str("show") == "en");
        if (Running && sig == _sig) { Status = StatusText(engine); return; }
        Stop();
        _sig = "";
        if (!c.Bool("enabled") && !voice) { Status = "выключены"; return; }
        if (model == null) { Status = "нужно скачать модель"; return; }   // сами ничего не скачиваем
        try { Start(engine, model, c); _sig = sig; CurrentModel = model; }
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
        if (on && _hub.Models.ForCaptions() == null)
        {
            _hub.Models.NeedModel();   // панель покажет «Сначала скачайте модель распознавания» с переходом к карточке
            throw new UserError("Сначала скачайте модель распознавания");
        }
        _hub.S.Set("captions.enabled", on);
        _hub.Settings.Save();
        Apply();
        _hub.Log.Add("system", on ? "Субтитры включены" : "Субтитры выключены");
        return on;
    }

    private void Start(string engine, SpeechModels.Model model, JsonObject c)
    {
        _cts = new CancellationTokenSource();
        _audio = new BlockingCollection<short[]>(new ConcurrentQueue<short[]>(), 400);
        var lang = c.Str("lang", "ru") == "en" ? "en" : "ru";
        var show = c.Str("show", "both");
        var token = _cts.Token;
        var path = SpeechModels.PathOf(model);
        if (engine == "whisper") PrepareRuntime(c.Bool("gpu", true));
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
        Status = StatusText(engine);
    }

    private string _sig = "";
    private string StatusText(string engine) =>
        (Cfg.Bool("enabled") ? "слушаю микрофон" : "слушаю только голосовые команды") + (engine == "vosk" ? " (Vosk)" : " (Whisper)");

    public void Stop()
    {
        CurrentModel = null;
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
            var loaded = RuntimeOptions.LoadedLibrary;
            _accel = AccelName(loaded);
            var wantGpu = _runtimeGpu == true;
            _hub.Loop.Post(() =>
            {
                _hub.Log.Add("system", $"Субтитры: Whisper работает на устройстве «{_accel}»");
                if (wantGpu && loaded is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx)
                    _hub.Log.Add("skip", "Субтитры: видеокарта недоступна (нет Vulkan или старый драйвер) — распознавание на процессоре. Обновите драйвер видеокарты.");
            });
            var threads = Math.Clamp(Cfg.Int("threads", 0) > 0 ? Cfg.Int("threads") : Environment.ProcessorCount / 2, 1, 16);
            WhisperProcessor transcribe = null, translate = null;
            if (show != "en" || lang == "en" || _hub.Voice.Wants)
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
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var text = transcribe != null ? await Run(transcribe, samples, ct) : "";
                    var en = translate != null ? await Run(translate, samples, ct) : (lang == "en" ? text : "");
                    _rtf = Math.Round(seconds / Math.Max(0.05, sw.Elapsed.TotalSeconds), 1);
                    if (_hub.Voice.OnFinal(text)) continue;   // голосовая команда — в субтитры не попадает
                    if (lang == "en") { en = text; text = show == "en" ? "" : text; }
                    else if (show == "en") text = "";
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
                    if (_hub.Voice.OnFinal(text)) continue;   // голосовая команда — в субтитры не попадает
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

    private void SetPartial(string p)
    {
        if (p.Length > 1 && _hub.Voice.HidePartial(p)) p = "";
        lock (_lock) _partial = Censor(p);
    }

    private void AddLine(string text, string en)
    {
        if (!Cfg.Bool("enabled")) return;   // распознавание идёт только ради голосовых команд
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
        var model = CurrentModel;
        var engine = model?.Engine ?? (c.Str("engine", "whisper") == "vosk" ? "vosk" : "whisper");
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
            engine, model = model?.Key ?? "", has_model = _hub.Models.ForCaptions() != null, voice = _hub.Voice.State(),
            speech = _hub.Models.State(),
            accel = Running ? (engine == "whisper" ? _accel : "процессор") : "",
            speed = engine == "whisper" && Running ? _rtf : 0,
            restart_needed = engine == "whisper" && _runtimeGpu.HasValue && _runtimeGpu.Value != c.Bool("gpu", true),
        };
    }
}
