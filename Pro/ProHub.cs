using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Features;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Pro;

/// <summary>
/// Дополнительные функции: свои темы оверлеев, озвучка, профили, расширенная статистика,
/// облачная копия, розыгрыши и мини-игры. Все бесплатные.
/// </summary>
public sealed class ProHub
{
    private static readonly Regex ColorRe = new(@"^(#[0-9a-fA-F]{3,8}|rgba?\([\d\s.,%]+\)|transparent)$");
    private static readonly Regex FontRe = new(@"^[\w\s,""'\-]{0,80}$");

    private readonly Hub _hub;
    public readonly Tts Tts;
    public readonly Giveaway Giveaway;
    public readonly Games Games;
    public readonly CloudSync Cloud;

    public ProHub(Hub hub)
    {
        _hub = hub;
        Tts = new Tts(hub);
        Giveaway = new Giveaway(hub);
        Games = new Games(hub);
        Cloud = new CloudSync(hub);
    }

    public void Start()
    {
        _hub.Loop.Start(Tts.Run);
        _hub.Loop.Start(Giveaway.Run);
        _hub.Loop.Start(Cloud.Run);
    }

    // ---------- события ----------
    /// <summary>Сообщение чата. true — сообщение обработано розыгрышем, игрой или озвучкой (дальше не идёт).</summary>
    public async Task<bool> OnChat(ChatMsg msg)
    {
        if (await Giveaway.OnChat(msg)) return true;
        if (_hub.S.Bool("pro.games.enabled") && await Games.OnChat(msg)) return true;
        var t = msg.Text.Trim();
        if (t.StartsWith("!"))
        {
            var parts = t.Split(' ', 2);
            var cmd = parts[0].ToLowerInvariant();
            if (cmd is "!tts" or "!say" or "!озвучь")
            {
                var reply = Tts.OnCommand(msg, parts.Length > 1 ? parts[1] : "");
                if (reply != null) await _hub.Twitch.SendChat(reply);
                return true;
            }
            if (cmd is "!розыгрыш" or "!giveaway" && Giveaway.Status == "open")
            {
                var how = Giveaway.Keyword != "" ? $"напишите {Giveaway.Keyword}" : $"купите награду «{Giveaway.RewardTitle}»";
                await _hub.Twitch.SendChat($"🎉 Сейчас разыгрываем: {Giveaway.Prize}. Чтобы участвовать, {how}. Участников: {Giveaway.Entries.Count}");
                return true;
            }
        }
        return false;
    }

    private static double Num(Dictionary<string, string> v, string key) =>
        v.TryGetValue(key, out var s) && double.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>Событие канала: статистика стримов и озвучка.</summary>
    public void OnEvent(string type, Dictionary<string, string> v)
    {
        var user = v.GetValueOrDefault("user", "");
        try
        {
            switch (type)
            {
                case "donation": _hub.Stats.Event(_hub.Stream.Key, type, user, Num(v, "amount"), v.GetValueOrDefault("currency", "")); break;
                case "cheer": _hub.Stats.Event(_hub.Stream.Key, type, user, Num(v, "bits"), ""); break;
                case "sub": _hub.Stats.Event(_hub.Stream.Key, type, user, Math.Max(1, Num(v, "count")), ""); break;
                case "raid": _hub.Stats.Event(_hub.Stream.Key, type, user, Num(v, "viewers"), ""); break;
                case "follow": _hub.Stats.Event(_hub.Stream.Key, type, user, 1, ""); break;
            }
        }
        catch (Exception e) { _hub.Log.Error("Статистика событий: " + e.Message); }
        if (type == "donation") Tts.OnDonation(user, v.GetValueOrDefault("amount", ""), v.GetValueOrDefault("currency", ""), v.GetValueOrDefault("message", ""));
        else if (type == "cheer") Tts.OnCheer(user, v.GetValueOrDefault("bits", ""), v.GetValueOrDefault("message", ""));
    }

    /// <summary>Награда за баллы, которой не управляет программа: озвучка или участие в розыгрыше.</summary>
    public async Task OnRedemption(JsonNode ev)
    {
        if (await Giveaway.OnReward(ev)) return;
        Tts.OnReward(ev.Str("reward.title"), ev.Str("user_name"), ev.Str("user_input"));
    }

    // ---------- свои темы оверлеев ----------
    /// <summary>Свои темы для страниц OBS.</summary>
    public JsonArray ThemesFor(string kind)
    {
        var res = new JsonArray();
        foreach (var t in _hub.S.Arr("pro.themes") ?? new JsonArray())
            if (t.Str("kind") == kind) res.Add(t.DeepClone());
        return res;
    }

    private static string Color(JsonNode t, string key, string def)
    {
        var v = t.Str(key).Trim();
        if (v == "") return def;
        if (!ColorRe.IsMatch(v)) throw new UserError($"Цвет «{v}» не распознан: используйте #rrggbb, #rrggbbaa или rgba(…)", "theme." + key);
        return v;
    }

    private static string CleanCss(string css)
    {
        css ??= "";
        if (css.Length > 8000) throw new UserError("Свой CSS длиннее 8000 символов", "theme.css");
        // CSS вставляется внутрь <style>: закрывающий тег и внешние загрузки не допускаются
        css = Regex.Replace(css, "</?\\s*style", "", RegexOptions.IgnoreCase);
        css = Regex.Replace(css, "@import[^;]*;?", "", RegexOptions.IgnoreCase);
        return css;
    }

    public JsonObject SaveTheme(JsonNode b)
    {
        var kind = b.Str("kind");
        if (kind != "player" && kind != "chat") throw new UserError("Неизвестный тип темы");
        var name = b.Str("name").Trim();
        if (name == "" || name.Length > 40) throw new UserError("Название темы: от 1 до 40 символов", "theme.name");
        var bases = kind == "player"
            ? new[] { "classic", "minimal", "neon", "vinyl", "bar", "glass", "retro" }
            : new[] { "classic", "cards", "bubbles", "neon", "retro", "compact", "light" };
        var bs = b.Str("base", "classic");
        if (!bases.Contains(bs)) bs = "classic";
        var font = b.Str("font").Trim();
        if (!FontRe.IsMatch(font)) throw new UserError("Название шрифта: только буквы, цифры, пробелы, запятые и кавычки", "theme.font");
        var shadow = b.Str("shadow", "soft");
        if (shadow is not ("none" or "soft" or "strong" or "glow")) shadow = "soft";
        var id = b.Str("id");
        var list = _hub.S.Arr("pro.themes");
        if (list == null)
        {
            _hub.S.Set("pro.themes", new JsonArray());
            list = _hub.S.Arr("pro.themes");
        }
        if (id == "" || list.All(t => t.Str("id") != id)) id = Guid.NewGuid().ToString("N")[..8];
        if (list.Any(t => t.Str("id") != id && t.Str("kind") == kind && string.Equals(t.Str("name"), name, StringComparison.OrdinalIgnoreCase)))
            throw new UserError($"Тема «{name}» уже есть — выберите другое название", "theme.name");
        var theme = new JsonObject
        {
            ["id"] = id, ["kind"] = kind, ["name"] = name, ["base"] = bs,
            ["accent"] = Color(b, "accent", "#9146ff"), ["background"] = Color(b, "background", "rgba(16,16,22,0.78)"),
            ["text_color"] = Color(b, "text_color", "#ffffff"), ["border_color"] = Color(b, "border_color", "transparent"),
            ["font"] = font, ["radius"] = Math.Max(0, Math.Min(60, b.Int("radius", 14))),
            ["border_width"] = Math.Max(0, Math.Min(12, b.Int("border_width", 0))),
            ["padding"] = Math.Max(0, Math.Min(40, b.Int("padding", 14))),
            ["shadow"] = shadow, ["uppercase"] = b.Bool("uppercase"), ["css"] = CleanCss(b.Str("css")),
        };
        var idx = list.Select((t, i) => (t, i)).FirstOrDefault(x => x.t.Str("id") == id);
        if (idx.t != null) list[idx.i] = theme;
        else list.Add(theme);
        _hub.Settings.Save();
        return (JsonObject)theme.DeepClone();
    }

    public void DeleteTheme(string id)
    {
        var list = _hub.S.Arr("pro.themes");
        var t = list?.FirstOrDefault(x => x.Str("id") == id);
        if (t == null) return;
        list.Remove(t);
        // если тема была выбрана — возвращаем базовую
        var reference = "custom:" + id;
        if (_hub.S.Str("overlay.theme") == reference) _hub.S.Set("overlay.theme", t.Str("base", "classic"));
        if (_hub.S.Str("chat_overlay.theme") == reference) _hub.S.Set("chat_overlay.theme", t.Str("base", "classic"));
        _hub.Settings.Save();
    }

    public object State() => new
    {
        profile = Config.ProfileName == "" ? Profiles.MainTitle : Config.ProfileName,
    };
}
