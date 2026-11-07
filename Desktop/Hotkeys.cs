using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Desktop;

/// <summary>Глобальные горячие клавиши через RegisterHotKey (раздел 15).</summary>
public static class Hotkeys
{
    public static readonly Dictionary<string, (string Title, string Default)> Actions = new()
    {
        ["pause_music"] = ("Пауза или продолжение музыки", "ctrl+alt+p"),
        ["skip"] = ("Пропуск текущей композиции", "ctrl+alt+right"),
        ["stop_alerts"] = ("Остановка МедиаАлертов", "ctrl+alt+backspace"),
        ["pause_events"] = ("Пауза или возобновление уведомлений о событиях", "ctrl+alt+n"),
    };

    private static readonly Dictionary<string, uint> Mods = new()
    {
        ["ctrl"] = 0x2, ["control"] = 0x2, ["alt"] = 0x1, ["shift"] = 0x4, ["win"] = 0x8, ["windows"] = 0x8,
    };

    private static readonly Dictionary<string, uint> Keys = BuildKeys();

    private static Dictionary<string, uint> BuildKeys()
    {
        var k = new Dictionary<string, uint>
        {
            ["right"] = 0x27, ["left"] = 0x25, ["up"] = 0x26, ["down"] = 0x28, ["backspace"] = 0x08, ["space"] = 0x20,
            ["enter"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
            ["-"] = 0xBD, ["="] = 0xBB, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, [";"] = 0xBA, ["'"] = 0xDE,
            ["["] = 0xDB, ["]"] = 0xDD, ["`"] = 0xC0, ["pause"] = 0x13,
        };
        for (var c = 'a'; c <= 'z'; c++) k[c.ToString()] = (uint)char.ToUpperInvariant(c);
        for (var d = 0; d <= 9; d++) { k[d.ToString()] = (uint)(0x30 + d); k["num" + d] = (uint)(0x60 + d); }
        for (var f = 1; f <= 24; f++) k["f" + f] = (uint)(0x6F + f);
        return k;
    }

    /// <summary>«Ctrl + Alt + P» → «ctrl+alt+p». Бросает UserError при неверной комбинации.</summary>
    public static string Normalize(string combo)
    {
        if (!TryParse(combo, out _, out _, out var norm))
            throw new UserError($"«{combo}»: используйте Ctrl, Alt, Shift или Win и обычную клавишу, например ctrl+alt+p");
        return norm;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk, out string normalized)
    {
        mods = 0; vk = 0; normalized = "";
        var parts = (combo ?? "").Replace(" ", "").ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count < 2) return false;
        var key = parts[^1];
        var modNames = parts.Take(parts.Count - 1).Select(p => p == "control" ? "ctrl" : p == "windows" ? "win" : p).ToList();
        foreach (var m in modNames)
        {
            if (!Mods.TryGetValue(m, out var bit)) return false;
            mods |= bit;
        }
        if (mods == 0 || Mods.ContainsKey(key) || !Keys.TryGetValue(key, out vk)) return false;
        normalized = string.Join("+", modNames.Distinct().Append(key));
        return true;
    }
}

/// <summary>Регистрирует комбинации на окне и вызывает действия.</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Action<string> _onAction;
    private readonly Dictionary<int, string> _ids = new();
    private readonly object _lock = new();
    private Dictionary<string, string> _status = new();

    public HotkeyManager(Window window, Action<string> onAction)
    {
        _onAction = onAction;
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
    }

    public Dictionary<string, string> Status
    {
        get { lock (_lock) return new Dictionary<string, string>(_status); }
    }

    private void UnregisterAll()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_hwnd, id);
        _ids.Clear();
    }

    /// <summary>Вызывать в UI-потоке.</summary>
    public void Apply(JsonNode cfg, ActionLog log)
    {
        UnregisterAll();
        var status = new Dictionary<string, string>();
        if (!cfg.Bool("enabled"))
        {
            foreach (var k in Hotkeys.Actions.Keys) status[k] = "выключены";
        }
        else
        {
            var nextId = 1;
            foreach (var name in Hotkeys.Actions.Keys)
            {
                var combo = cfg.Str(name);
                if (combo == "") { status[name] = "не задано"; continue; }
                if (!Hotkeys.TryParse(combo, out var mods, out var vk, out _)) { status[name] = "неверная комбинация"; continue; }
                var id = nextId++;
                if (RegisterHotKey(_hwnd, id, mods | ModNoRepeat, vk))
                {
                    _ids[id] = name;
                    status[name] = "работает";
                }
                else
                {
                    status[name] = "занята другой программой";
                    log.Error($"Горячая клавиша {combo} занята другой программой");
                }
            }
        }
        lock (_lock) _status = status;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _ids.TryGetValue(wParam.ToInt32(), out var name))
        {
            handled = true;
            _onAction(name);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
    }
}
