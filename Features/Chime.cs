using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Мягкий звуковой сигнал о новом заказе музыки. Звук синтезируется в памяти (без файлов):
/// синусоиды с плавной атакой и затуханием — ничего резкого.
/// </summary>
public sealed class Chime
{
    public static readonly Dictionary<string, string> Styles = new()
    {
        ["bell"] = "Колокольчик",
        ["marimba"] = "Маримба",
        ["soft"] = "Мягкий аккорд",
        ["drop"] = "Капля",
        ["chimes"] = "Колокольчики",
        ["harp"] = "Арфа",
        ["kalimba"] = "Калимба",
        ["glass"] = "Хрусталь",
        ["pluck"] = "Гитара",
        ["notify"] = "Уведомление",
        ["coin"] = "Монетка",
        ["bubble"] = "Пузырёк",
        ["magic"] = "Волшебство",
        ["doorbell"] = "Дверной звонок",
    };

    private const int Rate = 48000;
    private static readonly Dictionary<string, float[]> Cache = new();
    private static readonly object CacheLock = new();
    // держим ссылки, пока звук играет, чтобы вывод не собрал GC
    private readonly HashSet<WasapiOut> _active = new();

    private DateTime _last = DateTime.MinValue;

    /// <summary>Проигрывает сигнал. volume — 0…100. Не чаще раза в 0,4 с, чтобы серия заказов не сливалась в шум.</summary>
    public void Play(string style, int volume, string deviceId, bool force = false)
    {
        if (volume <= 0) return;
        if (!force && (DateTime.UtcNow - _last).TotalMilliseconds < 400) return;
        _last = DateTime.UtcNow;
        var data = Get(Styles.ContainsKey(style) ? style : "bell");
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        var stream = new RawSourceWaveStream(bytes, 0, bytes.Length, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        var v = Math.Min(100, volume) / 100f;
        var vol = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = v * v };
        var device = Player.DeviceById(deviceId);
        var o = device != null
            ? new WasapiOut(device, AudioClientShareMode.Shared, true, 100)
            : new WasapiOut(AudioClientShareMode.Shared, true, 100);
        lock (_active) _active.Add(o);
        o.PlaybackStopped += (s, e) => Task.Run(() =>
        {
            lock (_active) _active.Remove(o);
            try { o.Dispose(); } catch { }
            try { stream.Dispose(); } catch { }
        });
        try
        {
            o.Init(vol);
            o.Play();
        }
        catch
        {
            lock (_active) _active.Remove(o);
            try { o.Dispose(); } catch { }
            stream.Dispose();
            throw;
        }
    }

    private static float[] Get(string style)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(style, out var d)) Cache[style] = d = Render(style);
            return d;
        }
    }

    /// <summary>Синтез: ноты (время, частота, громкость) × обертоны (кратность, громкость, относительное затухание).</summary>
    private static float[] Render(string style)
    {
        (double T, double F, double A)[] notes;
        (double Mul, double A, double Decay)[] partials;
        double tau, attack;
        switch (style)
        {
            case "marimba":   // деревянный «тук-тук», ми → си
                notes = new[] { (0.0, 659.25, 0.9), (0.10, 987.77, 0.8) };
                partials = new[] { (1.0, 1.0, 1.0), (4.0, 0.12, 0.25), (10.0, 0.03, 0.1) };
                tau = 0.22; attack = 0.003;
                break;
            case "soft":      // тихое арпеджио до-мажора
                notes = new[] { (0.0, 523.25, 0.55), (0.08, 659.25, 0.55), (0.16, 783.99, 0.55), (0.26, 1046.5, 0.4) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.1, 0.5) };
                tau = 0.7; attack = 0.02;
                break;
            case "drop":      // «капля»: одна нота с лёгким скольжением вверх
                notes = new[] { (0.0, 880.0, 1.0) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.08, 0.4) };
                tau = 0.18; attack = 0.004;
                break;
            case "chimes":    // три колокольчика вверх
                notes = new[] { (0.0, 1318.51, 0.6), (0.12, 1567.98, 0.6), (0.24, 2093.0, 0.5) };
                partials = new[] { (1.0, 1.0, 1.0), (2.76, 0.12, 0.35), (5.4, 0.05, 0.2) };
                tau = 0.6; attack = 0.003;
                break;
            case "harp":      // быстрое арпеджио, как по струнам арфы
                notes = new[] { (0.0, 523.25, 0.5), (0.05, 659.25, 0.5), (0.10, 783.99, 0.5), (0.15, 1046.5, 0.45), (0.20, 1318.51, 0.4) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.3, 0.5), (3.0, 0.1, 0.3) };
                tau = 0.5; attack = 0.002;
                break;
            case "kalimba":   // «металлические язычки»: ре → ля
                notes = new[] { (0.0, 587.33, 0.8), (0.13, 880.0, 0.7) };
                partials = new[] { (1.0, 1.0, 1.0), (5.4, 0.12, 0.15), (8.9, 0.04, 0.1) };
                tau = 0.45; attack = 0.002;
                break;
            case "glass":     // хрустальный звон
                notes = new[] { (0.0, 1760.0, 0.6), (0.15, 2217.46, 0.5) };
                partials = new[] { (1.0, 1.0, 1.0), (2.32, 0.25, 0.4), (4.25, 0.08, 0.25) };
                tau = 0.7; attack = 0.004;
                break;
            case "pluck":     // щипок струны: соль → ре
                notes = new[] { (0.0, 392.0, 0.9), (0.09, 587.33, 0.8) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.5, 0.4), (3.0, 0.25, 0.25), (4.0, 0.12, 0.15) };
                tau = 0.28; attack = 0.002;
                break;
            case "notify":    // короткое мягкое «динь-дон» вверх
                notes = new[] { (0.0, 880.0, 0.7), (0.11, 1318.51, 0.7) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.1, 0.5) };
                tau = 0.35; attack = 0.006;
                break;
            case "coin":      // игровая монетка, но мягкая
                notes = new[] { (0.0, 987.77, 0.7), (0.08, 1318.51, 0.8) };
                partials = new[] { (1.0, 1.0, 1.0), (3.0, 0.15, 0.3) };
                tau = 0.2; attack = 0.002;
                break;
            case "bubble":    // пузырёк: короткий «бульк» вверх
                notes = new[] { (0.0, 600.0, 1.0) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.05, 0.4) };
                tau = 0.12; attack = 0.003;
                break;
            case "magic":     // искорки: быстрые высокие ноты вверх
                notes = new[] { (0.0, 1046.5, 0.35), (0.06, 1318.51, 0.35), (0.12, 1567.98, 0.35), (0.18, 2093.0, 0.35), (0.24, 2637.02, 0.3) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.15, 0.4) };
                tau = 0.45; attack = 0.003;
                break;
            case "doorbell":  // дверной звонок: ми → до
                notes = new[] { (0.0, 659.25, 0.8), (0.35, 523.25, 0.8) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.2, 0.6), (3.0, 0.08, 0.4) };
                tau = 0.65; attack = 0.005;
                break;
            default:          // колокольчик: до → соль
                notes = new[] { (0.0, 1046.5, 0.8), (0.13, 1567.98, 0.7) };
                partials = new[] { (1.0, 1.0, 1.0), (2.0, 0.22, 0.6), (3.0, 0.07, 0.4) };
                tau = 0.55; attack = 0.004;
                break;
        }
        var len = notes.Max(n => n.T) + tau * 6;
        var n = (int)(len * Rate);
        var mono = new double[n];
        foreach (var note in notes)
        {
            var start = (int)(note.T * Rate);
            for (var i = start; i < n; i++)
            {
                var t = (i - start) / (double)Rate;
                var env = (t < attack ? t / attack : 1.0) * Math.Exp(-t / tau);
                if (env < 1e-4 && t > attack) break;
                // фаза = ∫f(t)dt; у «капли» и «пузырька» частота плавно поднимается
                var (glide, gc) = style switch { "drop" => (0.5, 0.03), "bubble" => (0.8, 0.05), _ => (0.0, 1.0) };
                var phase = note.F * ((1 + glide) * t - glide * gc * (1 - Math.Exp(-t / gc)));
                double s = 0;
                foreach (var p in partials)
                    s += p.A * Math.Sin(2 * Math.PI * phase * p.Mul) * Math.Exp(-t / (tau * p.Decay) + t / tau);
                mono[i] += note.A * env * s;
            }
        }
        var peak = mono.Max(x => Math.Abs(x));
        var k = peak > 0 ? 0.7 / peak : 0;
        var fade = (int)(0.05 * Rate);
        var res = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var g = i > n - fade ? (n - i) / (double)fade : 1.0;
            res[2 * i] = res[2 * i + 1] = (float)(mono[i] * k * g);
        }
        return res;
    }
}
