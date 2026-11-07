using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Features;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Pro;

/// <summary>
/// Мини-игры в чате: «Угадай число» (запускает стример), дуэли и кости.
/// Баллы канала Twitch не выдаются — по правилам Twitch программа не может начислять их зрителям.
/// </summary>
public sealed class Games
{
    private static readonly Regex NumberRe = new(@"^\d{1,7}$");
    private readonly Hub _hub;
    private readonly Dictionary<string, double> _cooldowns = new();
    // вызов на дуэль: кого вызвали → (кто вызвал, когда)
    private readonly Dictionary<string, (string Login, string Name, double Ts)> _duels = new();

    public int GuessSecret;
    public int GuessMax;
    public int GuessTries;
    public double GuessStarted;
    public string LastWinner = "";

    public Games(Hub hub) { _hub = hub; }

    private JsonObject Cfg => _hub.S.Obj("pro.games");
    public bool GuessActive => GuessSecret > 0;

    public object State() => new
    {
        guess = new { active = GuessActive, max = GuessMax, tries = GuessTries, last_winner = LastWinner },
        duels = _duels.Count,
    };

    public async Task StartGuess(int max)
    {
        if (GuessActive) throw new UserError("Игра уже идёт — дождитесь победителя или остановите её");
        GuessMax = Math.Max(10, Math.Min(1_000_000, max));
        GuessSecret = 1 + RandomNumberGenerator.GetInt32(GuessMax);
        GuessTries = 0;
        GuessStarted = ActionLog.Now;
        _hub.Log.Add("system", $"«Угадай число» от 1 до {GuessMax}: загадано {GuessSecret}");
        await _hub.Twitch.SendChat($"🔢 Игра «Угадай число»: я загадал число от 1 до {GuessMax}. Пишите в чат свои варианты!");
    }

    public async Task StopGuess()
    {
        if (!GuessActive) return;
        var n = GuessSecret;
        GuessSecret = 0;
        await _hub.Twitch.SendChat($"Игра остановлена. Было загадано число {n}.");
    }

    private bool Cooldown(ChatMsg msg, string game)
    {
        var cd = Math.Max(0, Cfg.Num("cooldown", 30));
        var key = game + ":" + msg.Login;
        var now = ActionLog.Now;
        if (!msg.IsMod && _cooldowns.TryGetValue(key, out var last) && now - last < cd) return true;
        _cooldowns[key] = now;
        return false;
    }

    private static int Roll() => 1 + RandomNumberGenerator.GetInt32(6);

    /// <summary>Сообщения чата для игр. true — сообщение обработано игрой.</summary>
    public async Task<bool> OnChat(ChatMsg msg)
    {
        var c = Cfg;
        var text = msg.Text.Trim();
        // «Угадай число»: ответом считается сообщение только из цифр
        if (GuessActive && NumberRe.IsMatch(text))
        {
            GuessTries++;
            if (int.TryParse(text, out var n) && n == GuessSecret)
            {
                var tries = GuessTries;
                var secs = (int)(ActionLog.Now - GuessStarted);
                GuessSecret = 0;
                LastWinner = msg.Name;
                await _hub.Twitch.SendChat($"🎉 @{msg.Name} угадал(а) число {n}! Попыток в чате: {tries}, время: {Commands.HumanDuration(secs)}.");
            }
            return true;
        }
        if (!text.StartsWith("!")) return false;
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        if (c.Bool("dice", true) && cmd is "!кости" or "!dice")
        {
            if (Cooldown(msg, "dice")) return true;
            int a = Roll(), b = Roll(), x = Roll(), y = Roll();
            var verdict = a + b > x + y ? "победа! 🎉" : a + b < x + y ? "бот сильнее 😈" : "ничья 🤝";
            await _hub.Twitch.SendChat($"🎲 {msg.Name}: {a}+{b}={a + b} против бота {x}+{y}={x + y} — {verdict}");
            return true;
        }

        if (c.Bool("duel", true) && cmd is "!дуэль" or "!duel")
        {
            var target = arg.TrimStart('@').Split(' ')[0].ToLowerInvariant();
            if (target == "") { await _hub.Twitch.SendChat($"{msg.Name}, вызовите соперника: !дуэль @ник"); return true; }
            if (target == msg.Login) { await _hub.Twitch.SendChat($"{msg.Name}, с собой дуэль не получится 🙂"); return true; }
            if (Cooldown(msg, "duel")) return true;
            _duels[target] = (msg.Login, msg.Name, ActionLog.Now);
            var timeout = Math.Max(15, Math.Min(300, c.Int("duel_timeout", 60)));
            await _hub.Twitch.SendChat($"⚔️ {msg.Name} вызывает @{target} на дуэль! Чтобы принять, напишите !принять в течение {timeout} сек.");
            return true;
        }

        if (c.Bool("duel", true) && cmd is "!принять" or "!accept")
        {
            if (!_duels.TryGetValue(msg.Login, out var d)) return true;
            _duels.Remove(msg.Login);
            var timeout = Math.Max(15, Math.Min(300, c.Int("duel_timeout", 60)));
            if (ActionLog.Now - d.Ts > timeout)
            {
                await _hub.Twitch.SendChat($"{msg.Name}, вызов от {d.Name} уже истёк");
                return true;
            }
            var first = RandomNumberGenerator.GetInt32(2) == 0;
            var winner = first ? d.Name : msg.Name;
            var loser = first ? msg.Name : d.Name;
            await _hub.Twitch.SendChat($"⚔️ {d.Name} и {msg.Name} сошлись в дуэли... Победил(а) {winner}! {loser}, в следующий раз повезёт.");
            return true;
        }
        return false;
    }
}
