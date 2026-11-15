using System;
using System.Collections.Generic;

namespace Beg1nnnerOS.Desktop;

/// <summary>Цвета системных частей окна (заголовок, диалог закрытия, меню в трее) под текущую тему панели.</summary>
public sealed record ChromePalette(string Bg, string Panel, string Fg, string Muted, string Accent, string AccentText, string OnAccent, bool Light);

public static class ChromeTheme
{
    public static readonly Dictionary<string, ChromePalette> Palettes = new()
    {
        ["studio"] = new("#09090C", "#111116", "#F4F4F7", "#B6B6C4", "#8B5CF6", "#C4B5FD", "#FFFFFF", false),
        ["twitch"] = new("#0F0F14", "#17171F", "#E8E8EF", "#9A9AAB", "#9146FF", "#B38BFF", "#FFFFFF", false),
        ["light"] = new("#F5F4F0", "#FFFFFF", "#1B1B20", "#4C4C58", "#6D28D9", "#5B21B6", "#FFFFFF", true),
        ["midnight"] = new("#0B1020", "#111A30", "#E3E9F7", "#8A97B5", "#3B82F6", "#7CB0FF", "#FFFFFF", false),
        ["neon"] = new("#07070D", "#0E0E18", "#F2EAFF", "#9C8FBF", "#FF2BD6", "#2DE2FF", "#FFFFFF", false),
        ["graphite"] = new("#121212", "#1B1B1B", "#ECECEC", "#9A9A9A", "#D4D4D4", "#FFFFFF", "#111111", false),
        ["sunset"] = new("#1A1013", "#24161B", "#FBEEE6", "#C09A92", "#FF7A45", "#FFB38A", "#111111", false),
        ["forest"] = new("#0D1411", "#131D18", "#E4F1E8", "#8FAB9A", "#22C55E", "#86EFAC", "#111111", false),
    };

    public static string Name { get; private set; } = "studio";
    public static ChromePalette Current => Palettes.TryGetValue(Name, out var p) ? p : Palettes["studio"];

    /// <summary>Тема сменилась — перекрасить меню трея и т.п.</summary>
    public static event Action Changed;

    public static void Set(string theme)
    {
        var n = Palettes.ContainsKey(theme ?? "") ? theme : "studio";
        if (n == Name) return;
        Name = n;
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Смешивает цвет a с b (t — доля b, 0…1). Возвращает #RRGGBB.</summary>
    public static string Mix(string a, string b, double t)
    {
        int P(string h, int i) => Convert.ToInt32(h.TrimStart('#').Substring(i, 2), 16);
        string C(int i) => ((int)Math.Round(P(a, i) * (1 - t) + P(b, i) * t)).ToString("X2");
        return "#" + C(0) + C(2) + C(4);
    }
}
