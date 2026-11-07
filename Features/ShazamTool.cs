using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Shazam;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Shazam: запись 10 секунд ВЫХОДНОГО звука выбранного устройства (WASAPI loopback, не микрофон)
/// и распознавание по алгоритму shazam-for-real.
/// </summary>
public sealed class ShazamTool
{
    private const int RecordSec = 10;
    private readonly Hub _hub;
    private Task<string> _task;
    public string State = "ожидание";
    public double LastTs;
    public string LastText = "";
    public bool LastOk;

    public ShazamTool(Hub hub) { _hub = hub; }

    public bool Enabled => _hub.S.Bool("tools.shazam.enabled");

    public static List<object> Devices()
    {
        var list = new List<object>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string def = null;
            try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new { id = d.ID, name = d.FriendlyName + (d.ID == def ? " (по умолчанию)" : "") });
        }
        catch { }
        return list;
    }

    public object LastInfo() => LastTs > 0 ? new { ts = LastTs, text = LastText, ok = LastOk } : null;

    /// <summary>Одновременные запросы разделяют одно распознавание; в пределах интервала — последний ответ.</summary>
    public Task<string> Recognize()
    {
        if (!Enabled) throw new InvalidOperationException("распознавание Shazam выключено");
        var interval = Math.Max(0, Math.Min(60, _hub.S.Int("tools.shazam.interval", 5)));
        if (LastOk && ActionLog.Now - LastTs < interval) return Task.FromResult(LastText);
        if (_task == null || _task.IsCompleted) _task = Do();
        return _task;
    }

    private async Task<string> Do()
    {
        State = "запись 10 сек…";
        try
        {
            var deviceId = _hub.S.Str("tools.shazam.device");
            var samples = await Task.Run(() => RecordLoopback(deviceId, RecordSec));
            State = "распознавание…";
            var result = await Task.Run(() => Identify(samples));
            var text = result != null && result.Success
                ? $"{result.Artist} — {result.Title}".Trim(' ', '—')
                : "Не удалось распознать песню";
            LastTs = ActionLog.Now;
            LastText = text;
            LastOk = result != null && result.Success;
            State = "готово";
            _hub.Log.Add("command", $"Shazam: {text}");
            return text;
        }
        catch (Exception e)
        {
            State = "ошибка: " + e.Message;
            LastTs = ActionLog.Now;
            LastText = "";
            LastOk = false;
            throw new InvalidOperationException($"не удалось распознать ({e.Message})");
        }
    }

    /// <summary>Записывает выход устройства и возвращает моно-сэмплы 16 кГц.</summary>
    private static float[] RecordLoopback(string deviceId, int seconds)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice device = null;
        if (!string.IsNullOrEmpty(deviceId))
            device = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).FirstOrDefault(d => d.ID == deviceId);
        device ??= en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var capture = new WasapiLoopbackCapture(device);
        var fmt = capture.WaveFormat;
        var mono = new List<float>(fmt.SampleRate * seconds);
        var channels = fmt.Channels;
        var bits = fmt.BitsPerSample;
        var isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (fmt.Encoding == WaveFormatEncoding.Extensible && bits == 32);
        var needed = fmt.SampleRate * seconds;
        var done = new System.Threading.ManualResetEventSlim();
        Exception error = null;
        var stopped = false;
        capture.DataAvailable += (s, e) =>
        {
            lock (mono)
            {
            if (stopped) return;
            var bytesPerSample = bits / 8;
            var frame = bytesPerSample * channels;
            for (var off = 0; off + frame <= e.BytesRecorded && mono.Count < needed; off += frame)
            {
                double sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    var p = off + c * bytesPerSample;
                    if (isFloat) sum += BitConverter.ToSingle(e.Buffer, p);
                    else if (bits == 16) sum += BitConverter.ToInt16(e.Buffer, p) / 32768.0;
                    else if (bits == 24) sum += ((e.Buffer[p] | (e.Buffer[p + 1] << 8) | ((sbyte)e.Buffer[p + 2] << 16))) / 8388608.0;
                    else if (bits == 32) sum += BitConverter.ToInt32(e.Buffer, p) / 2147483648.0;
                }
                mono.Add((float)(sum / channels));
            }
            if (mono.Count >= needed) done.Set();
            }
        };
        capture.RecordingStopped += (s, e) => { error = e.Exception; done.Set(); };
        capture.StartRecording();
        // Loopback присылает данные только когда что-то играет — ждём не дольше seconds + 5
        done.Wait(TimeSpan.FromSeconds(seconds + 5));
        float[] recorded;
        lock (mono)
        {
            stopped = true;
            recorded = mono.ToArray();
        }
        capture.StopRecording();
        if (error != null) throw error;
        if (recorded.Length < fmt.SampleRate * 3)
            throw new InvalidOperationException("на выбранном устройстве нет звука (запустите музыку)");
        // Ресемплинг в 16 кГц
        var source = new FloatArrayProvider(recorded, fmt.SampleRate);
        var resampled = new WdlResamplingSampleProvider(source, Analysis.SAMPLE_RATE);
        var outp = new List<float>();
        var buf = new float[4096];
        int n;
        while ((n = resampled.Read(buf, 0, buf.Length)) > 0)
            for (var i = 0; i < n; i++) outp.Add(buf[i]);
        return outp.ToArray();
    }

    private static ShazamResult Identify(float[] samples)
    {
        var analysis = new Analysis();
        var finder = new PeakFinder(analysis);
        var chunk = new float[Analysis.CHUNK_SIZE];
        for (var pos = 0; pos + Analysis.CHUNK_SIZE <= samples.Length; pos += Analysis.CHUNK_SIZE)
        {
            Array.Copy(samples, pos, chunk, 0, Analysis.CHUNK_SIZE);
            analysis.AddChunk(chunk);
        }
        var sig = Sig.Write(Analysis.SAMPLE_RATE, analysis.ProcessedSamples, finder);
        return ShazamApi.SendRequestAsync(Guid.NewGuid().ToString(), analysis.ProcessedMs, sig).GetAwaiter().GetResult();
    }

    private sealed class FloatArrayProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _pos;
        public WaveFormat WaveFormat { get; }

        public FloatArrayProvider(float[] data, int rate)
        {
            _data = data;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
