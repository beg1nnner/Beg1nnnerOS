using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Голосовые команды стримера. Отдельного распознавателя нет: подписка на тот же поток текста, что и у субтитров
/// (Captions вызывает <see cref="OnFinal"/> для каждой готовой фразы). Срабатывают только окончательные результаты,
/// поэтому промежуточный и финальный текст одной фразы не дают двойного срабатывания.
/// Настройки — в captions.voice (попадают в резервные копии, профили и облако вместе с разделом субтитров).
/// </summary>
public sealed class VoiceCommands
{
    /// <summary>Одна строка таблицы «фраза → действие». Чтобы добавить команду, достаточно новой строки.</summary>
    public sealed record Command(string Key, string Title, string[] Phrases, Func<Hub, Task<string>> Run);

    public static readonly Command[] Table =
    {
        new("skip", "Следующий трек",
            new[] { "следующий трек", "следующая песня", "пропусти трек", "пропусти", "скип", "next track", "next song", "skip track", "skip" },
            h => Task.FromResult(h.Music.Skip("голосовая команда") ? "Трек пропущен" : "Сейчас ничего не играет")),
        new("pause", "Пауза",
            new[] { "пауза", "поставь на паузу", "на паузу", "останови музыку", "pause", "pause music", "stop music" },
            h => Task.FromResult(h.Music.SetPaused(true) ? "Музыка на паузе" : "Не получилось поставить на паузу")),
        new("resume", "Продолжить",
            new[] { "продолжай", "продолжи", "включи музыку", "сними с паузы", "resume", "play music", "continue" },
            h => Task.FromResult(!h.Music.SetPaused(false) ? "Музыка продолжается" : "Не получилось продолжить")),
        new("clip", "Сделать клип",
            new[] { "сделай клип", "клип", "запиши клип", "make a clip", "clip that", "clip it" },
            async h =>
            {
                // то же, что !clip: ответ уходит в чат
                var name = h.Twitch.Streamer.Login;
                var reply = await h.Commands.ClipAs(name == "" ? "стример" : name);
                if (reply != "") await h.Twitch.SendChat(reply);
                return reply;
            }),
    };

    private const double CooldownSec = 3;
    private readonly Hub _hub;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _fired = new();
    private long _nextId = 1;
    private (long Id, string Text, string Command, string Result, bool Test, double Ts) _last;
    private DateTime _testUntil = DateTime.MinValue;

    public VoiceCommands(Hub hub) => _hub = hub;

    private JsonObject Cfg => _hub.S.Obj("captions.voice") ?? new JsonObject();
    public bool Enabled => Cfg.Bool("enabled");
    public bool Testing => DateTime.UtcNow < _testUntil;
    /// <summary>Нужно ли распознавание ради команд (даже если субтитры выключены).</summary>
    public bool Wants => Enabled || Testing;
    public bool HideFromCaptions => Cfg.Bool("hide", true);
    public string Wake => Norm(Cfg.Str("wake", "бот")) is { Length: > 0 } w ? w : "бот";

    // ---------- нормализация и нечёткое сравнение ----------
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant().Replace('ё', 'е'))
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static int Lev(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Похожесть 0…1 по расстоянию Левенштейна.</summary>
    private static double Sim(string a, string b) => 1.0 - (double)Lev(a, b) / Math.Max(1, Math.Max(a.Length, b.Length));

    private static bool WordLike(string word, string wake) =>
        word == wake || (wake.Length >= 3 && Lev(word, wake) <= 1) || (wake == "бот" && word is "bot" or "бо" or "ботик");

    private Dictionary<string, List<string>> Phrases()
    {
        var custom = Cfg.Obj("phrases");
        var res = new Dictionary<string, List<string>>();
        foreach (var c in Table)
        {
            var list = c.Phrases.Select(Norm).ToList();
            if (custom?[c.Key] is JsonArray arr)
                list.AddRange(arr.Select(x => Norm(x?.ToString() ?? "")).Where(x => x != ""));
            res[c.Key] = list.Distinct().ToList();
        }
        return res;
    }

    private bool CommandOn(string key) => Cfg.Obj("off") is not JsonObject off || !off.Bool(key);

    /// <summary>Ищет «бот …команда…». Возвращает (есть ли слово активации, ключ команды или null).</summary>
    public (bool Wake, Command Cmd) Detect(string text)
    {
        var words = Norm(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wake = Wake;
        var wakeWords = wake.Split(' ');
        // слово активации — в первых трёх словах фразы (перед ним может прозвучать «эй», «так»)
        var at = -1;
        for (var i = 0; i < Math.Min(3, words.Length); i++)
        {
            if (i + wakeWords.Length > words.Length) break;
            var ok = true;
            for (var k = 0; k < wakeWords.Length && ok; k++) ok = WordLike(words[i + k], wakeWords[k]);
            if (ok) { at = i; break; }
        }
        if (at < 0) return (false, null);
        var rest = words.Skip(at + wakeWords.Length).ToArray();
        if (rest.Length == 0) return (true, null);
        Command best = null;
        double bestScore = 0;
        foreach (var (key, list) in Phrases())
        {
            if (!CommandOn(key)) continue;
            foreach (var p in list)
            {
                var n = p.Split(' ').Length;
                // сравниваем с началом остатка той же длины в словах, плюс-минус одно слово
                for (var len = Math.Max(1, n - 1); len <= Math.Min(rest.Length, n + 1); len++)
                {
                    var s = Sim(string.Join(" ", rest.Take(len)), p);
                    if (s > bestScore) { bestScore = s; best = Table.First(c => c.Key == key); }
                }
            }
        }
        return bestScore >= 0.74 ? (true, best) : (true, null);
    }

    /// <summary>
    /// Вызывается из потока распознавания для каждой готовой фразы. Возвращает true, если фразу нужно скрыть из субтитров.
    /// </summary>
    public bool OnFinal(string text)
    {
        if (!Wants || string.IsNullOrWhiteSpace(text)) return false;
        var (wake, cmd) = Detect(text);
        var test = Testing && !Enabled;
        var id = 0L;
        lock (_lock)
        {
            id = _nextId++;
            _last = (id, text.Trim(), cmd?.Key ?? "", wake ? (cmd == null ? "команда не распознана" : test ? "проверка: команда не выполнялась" : "") : "нет слова активации", test, ActionLog.Now);
        }
        if (cmd == null || test) return wake && HideFromCaptions;
        lock (_lock)
        {
            if (_fired.TryGetValue(cmd.Key, out var t) && (DateTime.UtcNow - t).TotalSeconds < CooldownSec)
            {
                _last.Result = "повтор пропущен (3 с)";
                return HideFromCaptions;
            }
            _fired[cmd.Key] = DateTime.UtcNow;
        }
        _hub.Loop.Post(() => _hub.Loop.Start(async () =>
        {
            string result;
            try { result = await cmd.Run(_hub); }
            catch (Exception e) { result = "ошибка: " + e.Message; }
            lock (_lock) if (_last.Id == id) _last.Result = result;
            _hub.Log.Add("command", $"Голосовая команда «{cmd.Title}»: {result}");
            if (Cfg.Bool("chime", true))
                try { _hub.Music.Chime.Play(Cfg.Str("chime_style", "notify"), Cfg.Int("chime_volume", 40), _hub.S.Str("music.audio_device"), true); } catch { }
        }));
        return HideFromCaptions;
    }

    /// <summary>Скрыть промежуточный текст, похожий на команду, чтобы он не мелькал в субтитрах.</summary>
    public bool HidePartial(string partial) => Wants && HideFromCaptions && Detect(partial).Wake;

    /// <summary>«Проверить микрофон»: 20 секунд распознавания без выполнения команд.</summary>
    public void StartTest() => _testUntil = DateTime.UtcNow.AddSeconds(20);

    /// <summary>Коротко для /state: панель показывает тост, когда команда выполнилась.</summary>
    public object Last()
    {
        lock (_lock)
            return _last.Id == 0 || _last.Command == "" || _last.Test ? null
                : new { id = _last.Id, title = Table.First(c => c.Key == _last.Command).Title, result = _last.Result };
    }

    public object State()
    {
        var c = Cfg;
        (long Id, string Text, string Command, string Result, bool Test, double Ts) last;
        lock (_lock) last = _last;
        var custom = c.Obj("phrases");
        return new
        {
            enabled = c.Bool("enabled"), wake = c.Str("wake", "бот"), hide = c.Bool("hide", true), chime = c.Bool("chime", true),
            testing = Testing, test_left = Testing ? Math.Ceiling((_testUntil - DateTime.UtcNow).TotalSeconds) : 0,
            commands = Table.Select(x => new
            {
                key = x.Key, title = x.Title, phrases = x.Phrases, on = CommandOn(x.Key),
                custom = custom?[x.Key] is JsonArray a ? a.Select(v => v?.ToString() ?? "").Where(v => v != "").ToArray() : Array.Empty<string>(),
            }),
            last = last.Id == 0 ? null : new { id = last.Id, text = last.Text, command = last.Command, result = last.Result, test = last.Test, ts = last.Ts },
        };
    }
}
