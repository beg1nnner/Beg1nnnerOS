using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beg1nnnerOS.Core;

/// <summary>
/// Язык программы. Тексты в коде — на русском; при английском языке они переводятся «на выходе»
/// (сообщения в чат, посты в Telegram/Discord, окна Windows) по словарю wwwroot/i18n/en.json.
/// Страницы панели и OBS переводятся тем же словарём в браузере (i18n.js).
/// Порядок: точное совпадение → шаблон с {подстановками} → замена известных фрагментов внутри строки.
/// </summary>
public static class L
{
    public static string Lang { get; private set; } = "ru";
    public static bool En => Lang == "en";

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly List<(Regex Re, string En, string[] Names, string Lit)> Patterns = new();
    private static Regex _frags;
    private static readonly Dictionary<string, string> FragMap = new(StringComparer.Ordinal);
    private static readonly Regex HasCyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);
    private static readonly Regex Ph = new(@"\{([A-Za-z_][A-Za-z0-9_]*|\d+)\}", RegexOptions.Compiled);
    private static string _file = "";

    public static void SetLanguage(string lang, string dictFile)
    {
        Lang = lang == "en" ? "en" : "ru";
        if (Lang == "en" && dictFile != _file) Load(dictFile);
    }

    private static void Load(string file)
    {
        Exact.Clear(); Patterns.Clear(); FragMap.Clear(); _frags = null;
        _file = file;
        try
        {
            if (!File.Exists(file)) return;
            var js = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject;
            if (js == null) return;
            foreach (var kv in js)
            {
                var ru = kv.Key; var en = kv.Value?.GetValue<string>() ?? "";
                if (ru == "" || en == "") continue;
                Exact[ru] = en;
                if (Ph.IsMatch(ru)) AddPattern(ru, en);
                else if (ru.Length >= 1) FragMap[ru] = en;
            }
            // граница слова — только там, где фрагмент начинается или кончается буквой
            static bool Letter(char c) => char.IsLetterOrDigit(c);
            var alts = FragMap.Keys.OrderByDescending(k => k.Length).Select(k =>
                (Letter(k[0]) ? @"(?<![А-Яа-яЁёA-Za-z0-9])" : "") + Regex.Escape(k) + (Letter(k[^1]) ? @"(?![А-Яа-яЁё])" : ""));
            _frags = new Regex(string.Join("|", alts), RegexOptions.Compiled);
        }
        catch { }
    }

    private static void AddPattern(string ru, string en)
    {
        var names = new List<string>();
        var sb = new StringBuilder("^");
        var last = 0;
        var lit = "";
        foreach (Match m in Ph.Matches(ru))
        {
            var part = ru[last..m.Index];
            if (part.Length > lit.Length) lit = part;
            sb.Append(Regex.Escape(part));
            sb.Append("(.+?)");
            names.Add(m.Groups[1].Value);
            last = m.Index + m.Length;
        }
        var tail = ru[last..];
        if (tail.Length > lit.Length) lit = tail;
        sb.Append(Regex.Escape(tail)).Append('$');
        if (lit.Trim().Length < 3) return;   // шаблон почти без текста — опасно
        Patterns.Add((new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.Compiled), en, names.ToArray(), lit));
    }

    /// <summary>Перевод строки на текущий язык. Для русского возвращает как есть.</summary>
    public static string T(string s)
    {
        if (!En || string.IsNullOrEmpty(s) || !HasCyr.IsMatch(s)) return s;
        try { return Translate(s, 0); }
        catch { return s; }
    }

    private static string Translate(string s, int depth)
    {
        var lead = s.Length - s.TrimStart().Length;
        var core = s.Trim();
        var pre = s[..lead]; var post = s[(lead + core.Length)..];
        if (Exact.TryGetValue(core, out var ex)) return pre + ex + post;
        foreach (var (re, en, names, lit) in Patterns)
        {
            if (!core.Contains(lit, StringComparison.Ordinal)) continue;
            var m = re.Match(core);
            if (!m.Success) continue;
            var outp = en;
            for (var i = 0; i < names.Length; i++)
            {
                var v = m.Groups[i + 1].Value;
                // подставленное значение (причина ошибки и т. п.) тоже может быть русским
                if (depth < 2 && HasCyr.IsMatch(v)) v = Exact.TryGetValue(v.Trim(), out var vx) ? vx : TranslatePatternOnly(v, depth + 1);
                outp = outp.Replace("{" + names[i] + "}", v);
            }
            return pre + outp + post;
        }
        if (depth > 0 || _frags == null) return s;
        // короткие слова («с», «от», «и»…) переводим, только если после замены не осталось русских слов
        var outp2 = _frags.Replace(s, m => m.Value.Length > 3 || !HasCyr.IsMatch(m.Value) ? (FragMap.TryGetValue(m.Value, out var f) ? f : m.Value) : m.Value);
        if (HasCyr.IsMatch(outp2))
        {
            var rest = _frags.Replace(outp2, m => FragMap.TryGetValue(m.Value, out var f) ? f : m.Value);
            if (!HasCyr.IsMatch(rest)) outp2 = rest;
        }
        return outp2;
    }

    private static string TranslatePatternOnly(string v, int depth)
    {
        var r = Translate(v, depth);
        return r;
    }

    /// <summary>Только точный перевод (для значений по умолчанию: названий наград, шаблонов). Иначе — как есть.</summary>
    public static string TE(string s)
    {
        if (!En || string.IsNullOrEmpty(s)) return s;
        return Exact.TryGetValue(s.Trim(), out var e) ? e : s;
    }

    /// <summary>Множественное число: Plural(5, "трек", "трека", "треков") / en: track, tracks.</summary>
    public static string Pl(int n, string one, string few, string many)
    {
        if (En) return T(Math.Abs(n) == 1 ? one : many);
        var a = Math.Abs(n) % 100; var b = a % 10;
        if (a is > 10 and < 20) return many;
        return b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }
}
