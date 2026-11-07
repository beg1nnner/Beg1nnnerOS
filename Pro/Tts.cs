using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Features;
using Beg1nnnerOS.Twitch;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Beg1nnnerOS.Pro;

/// <summary>
/// Озвучка: донаты, биты, награда за баллы и команда чата.
/// Движки: голоса Windows (System.Speech, офлайн) и Yandex SpeechKit (живые голоса, нужен API-ключ Yandex Cloud).
/// Фразы звучат по очереди; на время озвучки музыка приглушается.
/// </summary>
public sealed class Tts
{
    public static readonly string[] YandexVoices =
        { "alena", "filipp", "ermil", "jane", "madirus", "omazh", "zahar", "dasha", "julia", "lera", "masha", "marina", "alexander", "kirill", "anton" };
    private const string YandexUrl = "https://tts.api.cloud.yandex.net/speech/v1/tts:synthesize";
    private static readonly Regex LinkRe = new(@"(https?://|www\.)\S+", RegexOptions.IgnoreCase);
    private static readonly Regex SpacesRe = new(@"\s{2,}");

    private readonly Hub _hub;
    private readonly Queue<(string Text, string Source)> _queue = new();
    private TaskCompletionSource<bool> _signal = new();
    private readonly Dictionary<string, double> _cooldowns = new();
    private WasapiOut _out;
    public string Status = "ожидание";
    public string Speaking = "";

    public Tts(Hub hub) { _hub = hub; }

    private JsonObject Cfg => _hub.S.Obj("pro.tts");
    public int QueueCount => _queue.Count;

    // ---------- источники ----------
    public void OnDonation(string user, string amount, string currency, string message)
    {
        var c = Cfg;
        if (!c.Bool("donations")) return;
        var sum = J.AsNum(JsonValue.Create(amount));
        if (sum < c.Num("min_donation")) return;
        var head = c.Bool("read_name") ? $"{user} задонатил {amount} {CurrencyWord(currency)}." : "";
        Say($"{head} {message}", "донат");
    }

    public void OnCheer(string user, string bits, string message)
    {
        var c = Cfg;
        if (!c.Bool("bits")) return;
        if (J.AsNum(JsonValue.Create(bits)) < c.Num("min_bits", 100)) return;
        var head = c.Bool("read_name") ? $"{user} отправил {bits} битс." : "";
        Say($"{head} {message}", "биты");
    }

    /// <summary>Награда за баллы с вводом текста; true — награда наша.</summary>
    public bool OnReward(string title, string user, string input)
    {
        var want = Cfg.Str("reward_title").Trim();
        if (want == "" || !string.Equals(want, title.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        Say((Cfg.Bool("read_name") ? $"{user} говорит: " : "") + input, "награда");
        return true;
    }

    /// <summary>Команда !tts / !say. Возвращает ответ для чата или null.</summary>
    public string OnCommand(ChatMsg msg, string text)
    {
        var c = Cfg;
        if (!c.Bool("command")) return null;
        if (!msg.Allowed(c.Str("command_access", "vip"))) return null;
        if (text.Trim() == "") return $"{msg.Name}, напишите текст: !tts привет всем";
        var cd = Math.Max(0, c.Num("command_cooldown", 30));
        var now = ActionLog.Now;
        if (!msg.IsMod && _cooldowns.TryGetValue(msg.Login, out var last) && now - last < cd)
            return null;
        _cooldowns[msg.Login] = now;
        Say((c.Bool("read_name") ? $"{msg.Name} говорит: " : "") + text, "команда");
        return null;
    }

    private static string CurrencyWord(string cur) => cur.ToUpperInvariant() switch
    {
        "RUB" => "рублей", "USD" => "долларов", "EUR" => "евро", "UAH" => "гривен", "KZT" => "тенге", "BYN" => "белорусских рублей",
        _ => cur,
    };

    public string Clean(string text)
    {
        var c = Cfg;
        text ??= "";
        if (c.Bool("skip_links", true)) text = LinkRe.Replace(text, " ссылка ");
        foreach (var w in c.StrList("banned_words"))
            if (text.Contains(w, StringComparison.OrdinalIgnoreCase)) return "";
        text = SpacesRe.Replace(text, " ").Trim();
        var max = Math.Max(20, Math.Min(1000, c.Int("max_length", 200)));
        if (text.Length > max) text = text[..max];
        return text;
    }

    public void Say(string text, string source)
    {
        if (!Cfg.Bool("enabled")) return;
        text = Clean(text);
        if (text == "") return;
        if (_queue.Count >= 30) { _hub.Log.Add("skip", "Озвучка: очередь переполнена, фраза пропущена"); return; }
        _queue.Enqueue((text, source));
        _hub.Log.Add("command", $"Озвучка ({source}): {text}");
        _signal.TrySetResult(true);
    }

    public void Skip()
    {
        try { _out?.Stop(); } catch { }
    }

    public void Clear()
    {
        _queue.Clear();
        Skip();
    }

    // ---------- синтез ----------
    private static List<object> _voicesCache;
    private static double _voicesAt;

    /// <summary>Установленные голоса Windows (список кэшируется на минуту).</summary>
    public static List<object> WindowsVoices()
    {
        if (_voicesCache != null && ActionLog.Now - _voicesAt < 60) return _voicesCache;
        var list = new List<object>();
        try
        {
            using var s = new SpeechSynthesizer();
            foreach (var v in s.GetInstalledVoices().Where(v => v.Enabled))
                list.Add(new { name = v.VoiceInfo.Name, culture = v.VoiceInfo.Culture?.Name ?? "", gender = v.VoiceInfo.Gender.ToString() });
        }
        catch { }
        _voicesCache = list;
        _voicesAt = ActionLog.Now;
        return list;
    }

    private Task<byte[]> SynthWindows(string text, string voice, int rate) => Task.Run(() =>
    {
        using var synth = new SpeechSynthesizer();
        if (voice != "")
        {
            try { synth.SelectVoice(voice); }
            catch (ArgumentException) { }
        }
        else
        {
            // по умолчанию — русский голос, если он установлен
            var ru = synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture?.Name == "ru-RU");
            if (ru != null) synth.SelectVoice(ru.VoiceInfo.Name);
        }
        synth.Rate = Math.Max(-10, Math.Min(10, rate));
        synth.Volume = 100;
        using var ms = new MemoryStream();
        synth.SetOutputToWaveStream(ms);
        synth.Speak(text);
        synth.SetOutputToNull();
        return ms.ToArray();
    });

    private async Task<byte[]> SynthYandex(string text, JsonObject c)
    {
        var key = c.Str("yandex_key").Trim();
        if (key == "") throw new InvalidOperationException("не указан API-ключ Yandex SpeechKit");
        var form = new Dictionary<string, string>
        {
            ["text"] = text, ["lang"] = "ru-RU", ["voice"] = c.Str("yandex_voice", "alena"),
            ["speed"] = Math.Max(0.5, Math.Min(2.0, c.Num("yandex_speed", 1.0))).ToString("0.0#", CultureInfo.InvariantCulture),
            ["format"] = "lpcm", ["sampleRateHertz"] = "48000",
        };
        if (c.Str("yandex_folder").Trim() != "") form["folderId"] = c.Str("yandex_folder").Trim();
        using var req = new HttpRequestMessage(HttpMethod.Post, YandexUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", key);
        using var resp = await _hub.Http.SendAsync(req);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var msg = System.Text.Encoding.UTF8.GetString(bytes);
            if ((int)resp.StatusCode is 401 or 403) throw new InvalidOperationException("Yandex SpeechKit не принял ключ — проверьте API-ключ и роль ai.speechkit-tts.user");
            throw new InvalidOperationException($"Yandex SpeechKit: {(int)resp.StatusCode} {(msg.Length > 150 ? msg[..150] : msg)}");
        }
        return bytes;
    }

    private static MMDevice FindDevice(string id)
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

    /// <summary>Проигрывает звук и ждёт окончания (в HubLoop продолжение вернётся в цикл).</summary>
    private async Task Play(WaveStream stream, float volume, string deviceId)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = volume };
        var device = FindDevice(deviceId);
        var output = device != null
            ? new WasapiOut(device, AudioClientShareMode.Shared, true, 200)
            : new WasapiOut(AudioClientShareMode.Shared, true, 200);
        output.PlaybackStopped += (s, e) => done.TrySetResult(true);
        _out = output;
        try
        {
            output.Init(provider);
            output.Play();
            // страховка: фраза не может звучать дольше двух минут
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        }
        finally
        {
            _out = null;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
            try { stream.Dispose(); } catch { }
        }
    }

    public async Task Speak(string text)
    {
        var c = Cfg;
        WaveStream stream;
        if (c.Str("engine", "windows") == "yandex")
        {
            var pcm = await SynthYandex(text, c);
            stream = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(48000, 16, 1));
        }
        else
        {
            var wav = await SynthWindows(text, c.Str("voice"), c.Int("rate", 0));
            stream = new WaveFileReader(new MemoryStream(wav));
        }
        var duck = c.Bool("duck_music", true);
        if (duck) _hub.Music.Player.Duck = 0.3f;
        try
        {
            Speaking = text;
            await Play(stream, Math.Max(0, Math.Min(100, c.Int("volume", 80))) / 100f, c.Str("device"));
        }
        finally
        {
            Speaking = "";
            if (duck) _hub.Music.Player.Duck = 1f;
        }
    }

    public async Task Run()
    {
        while (true)
        {
            if (_queue.Count == 0)
            {
                _signal = new TaskCompletionSource<bool>();
                Status = "ожидание";
                await _signal.Task;
                continue;
            }
            var (text, _) = _queue.Dequeue();
            if (!Cfg.Bool("enabled")) continue;
            Status = "говорит";
            try { await Speak(text); }
            catch (Exception e)
            {
                Status = "ошибка: " + e.Message;
                _hub.Log.Error("Озвучка: " + e.Message);
                await Task.Delay(1000);
            }
            await Task.Delay(400);
        }
    }

    public object State() => new
    {
        status = Status, speaking = Speaking, queue = _queue.Count,
        voices = WindowsVoices(), yandex_voices = YandexVoices,
    };
}
