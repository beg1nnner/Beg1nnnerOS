using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Измерение громкости трека по ITU-R BS.1770 / EBU R128 (LUFS) — тот же принцип, что у Spotify и YouTube.
/// K-фильтр (два биквада) → энергия блоков 400 мс с шагом 100 мс → стробирование −70 LUFS и −10 LU.
/// </summary>
public static class Loudness
{
    /// <summary>Возвращает (громкость в LUFS, пиковое значение 0…1) или null, если в файле тишина.</summary>
    public static (double Lufs, double Peak)? Measure(string path, int maxMinutes = 12)
    {
        using var reader = new MediaFoundationReader(path);
        var sp = reader.ToSampleProvider();
        int ch = sp.WaveFormat.Channels, fs = sp.WaveFormat.SampleRate;
        if (ch <= 0 || fs <= 0) return null;

        var (s1, s2) = Coeffs(fs);
        var f1 = new Biquad[ch];
        var f2 = new Biquad[ch];
        for (var c = 0; c < ch; c++) { f1[c] = new Biquad(s1); f2[c] = new Biquad(s2); }

        var hop = fs / 10;                 // 100 мс
        var hops = new List<double>();     // энергия каждого шага (сумма по каналам)
        double acc = 0, peak = 0;
        var inHop = 0;
        long frames = 0, maxFrames = (long)fs * 60 * maxMinutes;
        var buf = new float[fs * ch];      // ~1 с за чтение
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0 && frames < maxFrames)
        {
            for (var i = 0; i + ch <= n; i += ch)
            {
                for (var c = 0; c < ch; c++)
                {
                    double x = buf[i + c];
                    var ax = Math.Abs(x);
                    if (ax > peak) peak = ax;
                    var y = f2[c].Step(f1[c].Step(x));
                    acc += y * y;
                }
                if (++inHop == hop) { hops.Add(acc); acc = 0; inHop = 0; }
                frames++;
            }
        }
        if (hops.Count < 4) return null;

        // блоки 400 мс = 4 шага подряд
        var blocks = new double[hops.Count - 3];
        for (var i = 0; i < blocks.Length; i++)
            blocks[i] = (hops[i] + hops[i + 1] + hops[i + 2] + hops[i + 3]) / (4.0 * hop);

        double sum = 0; var cnt = 0;
        foreach (var b in blocks) if (L(b) > -70) { sum += b; cnt++; }
        if (cnt == 0) return null;
        var rel = L(sum / cnt) - 10;
        sum = 0; cnt = 0;
        foreach (var b in blocks) if (L(b) > -70 && L(b) > rel) { sum += b; cnt++; }
        if (cnt == 0) return null;
        return (Math.Round(L(sum / cnt), 2), peak);
    }

    private static double L(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare + 1e-20);

    private static ((double[] B, double[] A), (double[] B, double[] A)) Coeffs(int fs)
    {
        // Ступень 1: «полка» +4 дБ на высоких (модель головы)
        double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
        var k = Math.Tan(Math.PI * f0 / fs);
        var vh = Math.Pow(10, g / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + k / q + k * k;
        var s1 = (new[] { (vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0 },
                  new[] { 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0 });
        // Ступень 2: срез низких ниже ~38 Гц
        f0 = 38.13547087602444; q = 0.5003270373238773;
        k = Math.Tan(Math.PI * f0 / fs);
        a0 = 1 + k / q + k * k;
        var s2 = (new[] { 1.0, -2.0, 1.0 },
                  new[] { 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0 });
        return (s1, s2);
    }

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;
        public Biquad((double[] B, double[] A) c) { (_b0, _b1, _b2) = (c.B[0], c.B[1], c.B[2]); (_a1, _a2) = (c.A[0], c.A[1]); }
        public double Step(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}

/// <summary>Усиление трека + мягкий лимитер: громкие места не «хрипят», даже если тихий трек подняли.</summary>
public sealed class GainLimiter : ISampleProvider
{
    private readonly ISampleProvider _src;
    private const float Knee = 0.89f;   // ≈ −1 дБ: выше — плавное ограничение до 1.0
    public volatile float Gain = 1f;

    public GainLimiter(ISampleProvider src) => _src = src;
    public WaveFormat WaveFormat => _src.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var n = _src.Read(buffer, offset, count);
        var g = Gain;
        if (g == 1f) return n;
        for (var i = offset; i < offset + n; i++)
        {
            var x = buffer[i] * g;
            var ax = Math.Abs(x);
            if (ax > Knee)
                x = Math.Sign(x) * (Knee + (1 - Knee) * (float)Math.Tanh((ax - Knee) / (1 - Knee)));
            buffer[i] = x;
        }
        return n;
    }
}
