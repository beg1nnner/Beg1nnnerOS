using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Beg1nnnerOS.Features;

/// <summary>Локальный плеер: файл → MediaFoundation → громкость → WASAPI (выбранное устройство).</summary>
public sealed class Player
{
    private WasapiOut _out;
    private MediaFoundationReader _reader;
    private VolumeSampleProvider _vol;
    private GainLimiter _gain;
    private double _gainDb;
    private float _volume = 0.36f;
    private float _duck = 1f;
    private float _fade = 1f;   // плавное нарастание в начале трека (0…1)

    public int Gen;
    public bool Paused;
    public string DeviceId = "";

    /// <summary>Длина файла, с.</summary>
    public double Total
    {
        get
        {
            try { return _reader?.TotalTime.TotalSeconds ?? 0; }
            catch { return 0; }
        }
    }

    public double Position
    {
        get
        {
            try { return _reader?.CurrentTime.TotalSeconds ?? 0; }
            catch { return 0; }
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Max(0, Math.Min(1, value));
            if (_vol != null) _vol.Volume = _volume * _duck * _fade;
        }
    }

    /// <summary>Временное приглушение: 1 — без изменений.</summary>
    public float Duck
    {
        get => _duck;
        set
        {
            _duck = Math.Max(0, Math.Min(1, value));
            if (_vol != null) _vol.Volume = _volume * _duck * _fade;
        }
    }

    /// <summary>Выравнивание громкости текущего трека, дБ (меняется на лету).</summary>
    public double GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = value;
            if (_gain != null) _gain.Gain = (float)Math.Pow(10, value / 20);
        }
    }

    public static List<object> Devices()
    {
        var list = new List<object>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new { id = d.ID, name = d.FriendlyName });
        }
        catch { }
        return list;
    }

    private MMDevice FindDevice() => DeviceById(DeviceId);

    /// <summary>Устройство вывода по ID; null — системное по умолчанию.</summary>
    public static MMDevice DeviceById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                if (d.ID == id) return d;
        }
        catch { }
        return null;
    }

    /// <summary>Запускает воспроизведение. onEnd(gen, ошибка) вызывается в контексте, где создан плеер.</summary>
    public int Play(string path, Action<int, string> onEnd, double gainDb = 0, double startAt = 0, double fadeIn = 0)
    {
        Stop();
        var gen = ++Gen;
        _reader = new MediaFoundationReader(path);
        if (startAt > 0.5)
        {
            try { _reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(startAt, Math.Max(0, _reader.TotalTime.TotalSeconds - 1))); }
            catch { }
        }
        _gain = new GainLimiter(_reader.ToSampleProvider());
        GainDb = gainDb;
        _fade = fadeIn > 0.05 ? 0f : 1f;
        _vol = new VolumeSampleProvider(_gain) { Volume = _volume * _duck * _fade };
        var device = FindDevice();
        _out = device != null
            ? new WasapiOut(device, AudioClientShareMode.Shared, true, 200)
            : new WasapiOut(AudioClientShareMode.Shared, true, 200);
        _out.PlaybackStopped += (s, e) =>
        {
            if (gen == Gen) onEnd(gen, e.Exception?.Message ?? "");
        };
        _out.Init(_vol);
        _out.Play();
        Paused = false;
        if (fadeIn > 0.05) _ = FadeIn(_vol, fadeIn);
        return gen;
    }

    private async Task FadeIn(VolumeSampleProvider vol, double seconds)
    {
        var start = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(30).ConfigureAwait(false);
            if (_vol != vol) return;   // трек уже сменился
            var k = (float)Math.Min(1, (DateTime.UtcNow - start).TotalSeconds / seconds);
            _fade = k * k * (3 - 2 * k);   // мягкая S-кривая
            vol.Volume = _volume * _duck * _fade;
            if (k >= 1) return;
        }
    }

    /// <summary>
    /// Плавно гасит текущий трек за seconds и освобождает его, не вызывая onEnd.
    /// Плеер сразу свободен: следующий трек может стартовать поверх затухающего (кроссфейд).
    /// </summary>
    public void FadeOut(double seconds)
    {
        if (_out == null) { Stop(); return; }
        Gen++;
        var o = _out; var r = _reader; var vol = _vol;
        _out = null; _reader = null; _vol = null; _gain = null;
        Paused = false;
        _ = Task.Run(async () =>
        {
            try
            {
                var from = vol.Volume;
                var start = DateTime.UtcNow;
                while (true)
                {
                    await Task.Delay(30);
                    var k = Math.Min(1, (DateTime.UtcNow - start).TotalSeconds / Math.Max(0.05, seconds));
                    vol.Volume = (float)(from * (1 - k) * (1 - k));
                    if (k >= 1) break;
                }
            }
            catch { }
            try { o.Stop(); } catch { }
            try { o.Dispose(); } catch { }
            try { r?.Dispose(); } catch { }
        });
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (_out == null) return;
        try
        {
            if (paused) _out.Pause();
            else _out.Play();
        }
        catch { }
    }

    public void Stop()
    {
        Gen++;   // события остановки старого трека игнорируются
        var o = _out;
        var r = _reader;
        _out = null;
        _reader = null;
        _vol = null;
        _gain = null;
        Paused = false;
        try { o?.Stop(); } catch { }
        try { o?.Dispose(); } catch { }
        try { r?.Dispose(); } catch { }
    }
}
