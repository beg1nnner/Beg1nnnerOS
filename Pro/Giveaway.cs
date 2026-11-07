using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Features;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Pro;

/// <summary>
/// Розыгрыш: участие по слову в чате или покупкой награды за баллы, шанс выше у подписчиков,
/// честный случайный выбор (криптографический генератор), перевыбор победителя. Состояние переживает перезапуск.
/// </summary>
public sealed class Giveaway
{
    public sealed class Entry
    {
        public string Login = "";
        public string Name = "";
        public string UserId = "";
        public bool Sub;
        public int Tickets = 1;
        public double Ts;
    }

    private readonly Hub _hub;
    private readonly JsonStore _store = new(Path.Combine(Config.Profile, "giveaway.json"), new JsonObject());
    public string Status = "idle";          // idle | open | closed
    public string Prize = "";
    public string Keyword = "";
    public string RewardTitle = "";
    public bool SubsOnly, FollowersOnly;
    public int SubLuck = 1;
    public int WinnersWanted = 1;
    public double EndsAt;
    public readonly Dictionary<string, Entry> Entries = new();
    public readonly List<Entry> Winners = new();
    private readonly HashSet<string> _checking = new();

    public Giveaway(Hub hub)
    {
        _hub = hub;
        Load();
    }

    private void Load()
    {
        var d = _store.Data;
        Status = d.Str("status", "idle");
        Prize = d.Str("prize");
        Keyword = d.Str("keyword");
        RewardTitle = d.Str("reward_title");
        SubsOnly = d.Bool("subs_only");
        FollowersOnly = d.Bool("followers_only");
        SubLuck = Math.Max(1, d.Int("sub_luck", 1));
        WinnersWanted = Math.Max(1, d.Int("winners_wanted", 1));
        EndsAt = d.Num("ends_at");
        foreach (var e in d.Arr("entries") ?? new JsonArray())
            Entries[e.Str("login")] = new Entry
            {
                Login = e.Str("login"), Name = e.Str("name"), UserId = e.Str("user_id"), Sub = e.Bool("sub"),
                Tickets = Math.Max(1, e.Int("tickets", 1)), Ts = e.Num("ts"),
            };
        foreach (var w in d.Arr("winners") ?? new JsonArray())
            if (Entries.TryGetValue(w.Str("login"), out var en)) Winners.Add(en);
    }

    private void Save()
    {
        var entries = new JsonArray();
        foreach (var e in Entries.Values)
            entries.Add(new JsonObject
            {
                ["login"] = e.Login, ["name"] = e.Name, ["user_id"] = e.UserId, ["sub"] = e.Sub, ["tickets"] = e.Tickets, ["ts"] = e.Ts,
            });
        var winners = new JsonArray();
        foreach (var w in Winners) winners.Add(new JsonObject { ["login"] = w.Login });
        _store.Data = new JsonObject
        {
            ["status"] = Status, ["prize"] = Prize, ["keyword"] = Keyword, ["reward_title"] = RewardTitle,
            ["subs_only"] = SubsOnly, ["followers_only"] = FollowersOnly, ["sub_luck"] = SubLuck, ["winners"] = winners,
            ["ends_at"] = EndsAt, ["entries"] = entries,
        };
        _store.Data["winners_wanted"] = WinnersWanted;
        _store.Save();
    }

    public object State() => new
    {
        status = Status, prize = Prize, keyword = Keyword, reward_title = RewardTitle, subs_only = SubsOnly,
        followers_only = FollowersOnly, sub_luck = SubLuck, winners_wanted = WinnersWanted,
        ends_in = Status == "open" && EndsAt > 0 ? Math.Max(0, (int)(EndsAt - ActionLog.Now)) : 0,
        count = Entries.Count, tickets = Entries.Values.Sum(e => e.Tickets),
        entries = Entries.Values.OrderByDescending(e => e.Ts).Take(300)
            .Select(e => new { login = e.Login, name = e.Name, sub = e.Sub, tickets = e.Tickets }).ToList(),
        winners = Winners.Select(w => new { login = w.Login, name = w.Name, sub = w.Sub }).ToList(),
    };

    // ---------- управление ----------
    public async Task Start(JsonNode b)
    {
        if (Status == "open") throw new UserError("Розыгрыш уже идёт — сначала завершите его");
        var prize = b.Str("prize").Trim();
        if (prize == "") throw new UserError("Напишите, что разыгрываете — это увидят зрители", "giveaway.prize");
        var keyword = b.Str("keyword").Trim();
        var reward = b.Str("reward_title").Trim();
        if (keyword == "" && reward == "")
            throw new UserError("Укажите слово для участия (например !участвую) или название награды за баллы", "giveaway.keyword");
        if (keyword.Contains(' ')) throw new UserError("Слово для участия — одно слово без пробелов, например !участвую", "giveaway.keyword");
        Entries.Clear();
        Winners.Clear();
        Prize = prize.Length > 120 ? prize[..120] : prize;
        Keyword = keyword;
        RewardTitle = reward;
        SubsOnly = b.Bool("subs_only");
        FollowersOnly = b.Bool("followers_only");
        SubLuck = Math.Max(1, Math.Min(10, b.Int("sub_luck", 1)));
        WinnersWanted = Math.Max(1, Math.Min(20, b.Int("winners", 1)));
        var minutes = Math.Max(0, Math.Min(240, b.Int("minutes", 0)));
        EndsAt = minutes > 0 ? ActionLog.Now + minutes * 60 : 0;
        Status = "open";
        Save();
        var how = keyword != "" ? $"напишите в чат {keyword}" : $"купите награду «{reward}» за баллы";
        if (keyword != "" && reward != "") how += $" или купите награду «{reward}»";
        var extra = new List<string>();
        if (SubsOnly) extra.Add("только для подписчиков");
        else if (SubLuck > 1) extra.Add($"у подписчиков шанс ×{SubLuck}");
        if (FollowersOnly) extra.Add("нужно быть фолловером");
        if (minutes > 0) extra.Add($"приём заявок {minutes} мин");
        _hub.Log.Add("system", $"Розыгрыш начат: {Prize}");
        await _hub.Twitch.SendChat($"🎉 Розыгрыш: {Prize}! Чтобы участвовать, {how}." + (extra.Count > 0 ? $" ({string.Join(", ", extra)})" : ""));
    }

    public async Task Close(bool announce = true)
    {
        if (Status != "open") return;
        Status = "closed";
        EndsAt = 0;
        Save();
        if (announce) await _hub.Twitch.SendChat($"Приём заявок на розыгрыш «{Prize}» закрыт. Участников: {Entries.Count}. Скоро выберем победителя!");
    }

    /// <summary>Выбирает победителей (если розыгрыш открыт — сначала закрывает приём).</summary>
    public async Task<List<Entry>> Draw(int count = 0)
    {
        if (Status == "idle") throw new UserError("Сначала начните розыгрыш");
        if (Status == "open") await Close(false);
        count = count > 0 ? count : WinnersWanted;
        var picked = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var w = PickOne();
            if (w == null) break;
            Winners.Add(w);
            picked.Add(w);
        }
        Save();
        if (picked.Count == 0) throw new UserError("Нет участников, которые ещё не выигрывали");
        var names = string.Join(", ", picked.Select(p => "@" + p.Name));
        await _hub.Twitch.SendChat(picked.Count == 1
            ? $"🏆 Победитель розыгрыша «{Prize}»: {names}! Поздравляем!"
            : $"🏆 Победители розыгрыша «{Prize}»: {names}! Поздравляем!");
        _hub.Log.Add("system", $"Розыгрыш «{Prize}»: победители {names}");
        return picked;
    }

    /// <summary>Перевыбор: убирает победителя (не пришёл, не ответил) и выбирает другого.</summary>
    public async Task<Entry> Reroll(string login)
    {
        var old = Winners.FirstOrDefault(w => w.Login == login);
        if (old == null) throw new UserError("Этого зрителя нет среди победителей");
        Winners.Remove(old);
        Entries.Remove(old.Login);   // повторно он не выпадет
        var w = PickOne();
        Save();
        if (w == null)
        {
            await _hub.Twitch.SendChat($"Перевыбор: {old.Name} выбывает, но других участников не осталось");
            return null;
        }
        Winners.Add(w);
        Save();
        await _hub.Twitch.SendChat($"🔄 Перевыбор: новый победитель розыгрыша «{Prize}» — @{w.Name}!");
        return w;
    }

    public void Finish()
    {
        Status = "idle";
        Entries.Clear();
        Winners.Clear();
        EndsAt = 0;
        Save();
    }

    private Entry PickOne()
    {
        var pool = Entries.Values.Where(e => !Winners.Contains(e)).ToList();
        var total = pool.Sum(e => e.Tickets);
        if (total <= 0) return null;
        var r = RandomNumberGenerator.GetInt32(total);
        foreach (var e in pool)
        {
            if (r < e.Tickets) return e;
            r -= e.Tickets;
        }
        return pool[^1];
    }

    // ---------- участие ----------
    private async Task<bool> Add(string userId, string login, string name, bool isSub)
    {
        if (Status != "open" || Entries.ContainsKey(login) || _checking.Contains(login)) return false;
        if (SubsOnly && !isSub) return false;
        _checking.Add(login);
        try
        {
            if (FollowersOnly && login != _hub.Twitch.Streamer.Login)
            {
                try
                {
                    if (string.IsNullOrEmpty(await _hub.Twitch.GetFollowedAt(userId)))
                    {
                        _hub.Log.Add("skip", $"Розыгрыш: {login} не фолловер");
                        return false;
                    }
                }
                catch (TwitchException e) { _hub.Log.Add("skip", $"Розыгрыш: не удалось проверить фоллов {login}: {e.Reason}"); }
            }
            if (Status != "open") return false;
            Entries[login] = new Entry
            {
                Login = login, Name = name, UserId = userId, Sub = isSub, Tickets = isSub ? SubLuck : 1, Ts = ActionLog.Now,
            };
            Save();
            return true;
        }
        finally { _checking.Remove(login); }
    }

    public async Task<bool> OnChat(ChatMsg msg)
    {
        if (Status != "open" || Keyword == "") return false;
        if (!string.Equals(msg.Text.Trim(), Keyword, StringComparison.OrdinalIgnoreCase)) return false;
        await Add(msg.UserId, msg.Login, msg.Name, msg.IsSub);
        return true;   // слово участия не считается командой и не переводится
    }

    public async Task<bool> OnReward(JsonNode ev)
    {
        if (Status != "open" || RewardTitle == "") return false;
        if (!string.Equals(ev.Str("reward.title").Trim(), RewardTitle, StringComparison.OrdinalIgnoreCase)) return false;
        var uid = ev.Str("user_id");
        var sub = false;
        if (SubsOnly || SubLuck > 1)
        {
            try { sub = await _hub.Twitch.IsSubscriber(uid); }
            catch (TwitchException) { }
        }
        await Add(uid, ev.Str("user_login"), ev.Str("user_name"), sub);
        return true;
    }

    /// <summary>Автозакрытие приёма заявок по таймеру и выбор победителей.</summary>
    public async Task Run()
    {
        while (true)
        {
            await Task.Delay(1000);
            if (Status == "open" && EndsAt > 0 && ActionLog.Now >= EndsAt)
            {
                try
                {
                    if (Entries.Count == 0)
                    {
                        await Close(false);
                        await _hub.Twitch.SendChat($"Розыгрыш «{Prize}» завершён: участников не было");
                    }
                    else await Draw();
                }
                catch (Exception e) { _hub.Log.Error("Розыгрыш: " + e.Message); }
            }
        }
    }
}
