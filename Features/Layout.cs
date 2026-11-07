using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Исправление ошибочной RU/EN-раскладки: ghbdtn → привет, re → ку, kj[ → лох, руддщ → hello.
/// Решение принимается по словарям частых слов (40 000 русских и 25 000 английских, по частоте
/// в субтитрах — проект FrequencyWords, Hermit Dave) плюс сленг чата. Слово меняется, только если
/// в другой раскладке оно — известное слово, а в набранной — нет (или намного реже).
/// Незнакомые слова (ники, эмоуты) не трогаются.
/// </summary>
public static class Layout
{
    private const string EnKeys = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>";
    private const string RuKeys = "ёйцукенгшщзхъфывапролджэячсмитьбюЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ";
    private static readonly Dictionary<char, char> ToRu = Map(EnKeys, RuKeys);
    private static readonly Dictionary<char, char> ToEn = Map(RuKeys, EnKeys);

    // Сленг чата, которого может не быть в словарях. Ранг: чем меньше, тем «увереннее» слово.
    private const string SlangRu =
        "ку лол кек лох норм спс пасиб хз ща щас изи кринж кринге рофл рофлан имба пж плз го гг кк ок бб нуб нубас агр " +
        "агришься байт сорян сори лс мб нз оч чел чат стрим стример донат саб сабка фолов фоловер бан пермач мод модер " +
        "ахах ахаха ахахах хаха хахаха хех жиза жиз сасный сабж топ топчик ору пон понял збс пздц пипец капец блин блять " +
        "бля хрен нах ваще чё че щя тип типа кста имхо мда ясн ясно ауф вайб краш тильт тильтую душнила душный рил кайф " +
        "кайфово лмао гоу пойдём погнали некст скип скипни трек трэк зайди залетай";
    private const string SlangEn =
        "lol kek kekw lul xd gg wp ez pog poggers omegalul lmao rofl brb afk omg wtf idk imo btw np ty thx pls plz nice " +
        "gj hf gl sus bruh lmfao";

    private static readonly Lazy<Dictionary<string, int>> Ru = new(() => Load("words_ru", SlangRu, 3000));
    private static readonly Lazy<Dictionary<string, int>> En = new(() => Load("words_en", SlangEn, 100));

    // Запасной признак для слов не из словаря — частые сочетания двух букв.
    private static readonly HashSet<string> RuBigrams = new((
        "ст но то на ен ов ни ра во ко ал ро ер ли ре от пр по ла ть ет ос ка ол ан ле ор ит ел ва ин ог не ас ми ак ем ск ве " +
        "ат ом го ес аз ой ым ам од ди де ри ту мо ти та до да ед ив ия ий ая ый ую ят ег об ля чт ше хо вы пе бы же ме мн уж ду " +
        "ус ут эт ся ло лу ру ги ки ик че чи уч жи жа зд за зн бо бе би вл вс вн вр гд гл гр дн др ее ей ём жн зв им ис их ич кр " +
        "кт ку ль мы нн ны нь ож оз ок он оп оч пл пу рв рт ры са св се си сл см сп су сь те тр ты уд ул ум ха це ча ыв ые ых ют " +
        "ае аю ое ие ии па пи ба бу иб шо ши ша ще щи ещ ющ ящ ущ ён ёт ну ай уй юс ья ье ью ьё " +
        "тв ек ма ьз ьм зу зе вт ыш ыл ыт ыс ыр кл кн лк лн мк нк нд нт пт рк рн рм рс рх сс тк тн хв шл шн шт ыб " +
        "дя ды сё вё зо зи му мя ям лю лё ке еб бл рю ря юб юд яз ял ох ах ух").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static readonly HashSet<string> EnBigrams = new((
        "th he in er an re on at en nd ti es or te of ed is it al ar st to nt ng se ha as ou io le ve co me de hi ri ro ic ne ea " +
        "ra ce li ch ll be ma si om ur ca el ta la ns di fo ho pe ec pr no ct us ac ot il tr ly nc et ut ss so rs un lo wa ge ie " +
        "wh ee wi em ld rl wo rd nk ft gh ht ry ty ev ex ul op up ag ig ug ek br ru uh yp hy ow ay ps ep " +
        "ad ol rt po we na ni ts mo pa im mi ai sh ir su id os iv am oo ey yo ke ck ok go ga").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static readonly Regex EnWord = new("[A-Za-z`~;:'\"\\[\\]{}<>,.]+");
    private static readonly Regex RuWord = new("[А-Яа-яЁё]+");

    private enum Kind { Neutral, Convert, Weak, Keep, Ambig, Guess, Unknown }

    private sealed record Item(Kind Kind, string Orig, string Conv, string Trail);

    private static Dictionary<char, char> Map(string from, string to)
    {
        var d = new Dictionary<char, char>();
        for (var i = 0; i < from.Length; i++) d[from[i]] = to[i];
        return d;
    }

    private static Dictionary<string, int> Load(string name, string slang, int slangRank)
    {
        var d = new Dictionary<string, int>(70000);
        try
        {
            using var s = typeof(Layout).Assembly.GetManifestResourceStream($"Beg1nnnerOS.Data.{name}.txt.gz");
            if (s != null)
            {
                using var gz = new GZipStream(s, CompressionMode.Decompress);
                using var r = new StreamReader(gz, Encoding.UTF8);
                string line;
                while ((line = r.ReadLine()) != null)
                    if (line != "" && !d.ContainsKey(line)) d[line] = d.Count + 1;
            }
        }
        catch { /* без словаря работает только сленг и запасной признак */ }
        foreach (var w in slang.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            d.TryAdd(w, slangRank);
        return d;
    }

    private static string Translate(string s, Dictionary<char, char> map)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(map.TryGetValue(c, out var r) ? r : c);
        return sb.ToString();
    }

    private static int Rank(Dictionary<string, int> d, string w) =>
        d.TryGetValue(w.ToLowerInvariant(), out var r) ? r : 0;

    private static double Score(string word, HashSet<string> table)
    {
        var w = word.ToLowerInvariant();
        if (w.Length < 2) return 0;
        var hits = 0;
        for (var i = 0; i < w.Length - 1; i++)
            if (table.Contains(w.Substring(i, 2))) hits++;
        return hits / (double)(w.Length - 1);
    }

    private static bool AllUpper(string s) => s.Any(char.IsLetter) && s.Where(char.IsLetter).All(char.IsUpper);

    private static (Kind, string) Classify(string core, bool enSource)
    {
        var (src, dst, map, bs, bd) = enSource
            ? (En.Value, Ru.Value, ToRu, EnBigrams, RuBigrams)
            : (Ru.Value, En.Value, ToEn, RuBigrams, EnBigrams);
        if (!core.Any(char.IsLetter)) return (Kind.Neutral, core);
        // PogChamp, monkaS — смешанный регистр: эмоут, не трогаем
        if (core.Skip(1).Any(char.IsUpper) && !AllUpper(core)) return (Kind.Neutral, core);
        var conv = Translate(core, map);
        int rs = Rank(src, core), rd = Rank(dst, conv);
        if (rd > 0 && rs == 0) return (conv.Length >= 2 ? Kind.Convert : Kind.Weak, conv);
        if (rs > 0 && rd == 0) return (Kind.Keep, conv);
        if (rs > 0 && rd > 0)
        {
            if (rd * 20 < rs) return (Kind.Convert, conv);   // «ye» → «ну»: «ну» намного частотнее
            if (rs * 20 < rd) return (Kind.Keep, conv);      // «of» остаётся «of», а не «ща»
            return (Kind.Ambig, conv);
        }
        if (AllUpper(core) && core.Length > 1) return (Kind.Neutral, conv);   // KEKW и прочие эмоуты
        if (core.Length >= 4 && Score(conv, bd) >= 0.6 && Score(core, bs) <= 0.4) return (Kind.Guess, conv);
        return (Kind.Unknown, conv);
    }

    /// <summary>Возвращает исправленный текст при уверенном обнаружении ошибочной раскладки, иначе null.</summary>
    public static string Fix(string text)
    {
        var t = (text ?? "").Trim();
        if (t == "" || t.StartsWith("!") || Regex.IsMatch(t, @"https?://|www\.|@\w")) return null;
        var hasRu = RuWord.IsMatch(t);
        var hasEn = Regex.IsMatch(t, "[A-Za-z]");
        if (hasEn == hasRu) return null;   // смешанный текст или вообще без букв — не трогаем
        var en = hasEn;

        var items = new List<Item>();
        var gaps = new List<string>();
        var pos = 0;
        foreach (Match m in (en ? EnWord : RuWord).Matches(t))
        {
            gaps.Add(t[pos..m.Index]);
            pos = m.Index + m.Length;
            var tok = m.Value;
            if (!en) { var (k, c) = Classify(tok, false); items.Add(new Item(k, tok, c, "")); continue; }

            // «ghbdtn,» — запятая в конце может быть и буквой «б», и знаком препинания: выбираем по словарю
            var lead = Regex.Match(tok, "^[\"<]*").Value;
            var core = tok[lead.Length..];
            var trail = Regex.Match(core, "[,.!?\"]*$").Value;
            if (trail != "") core = core[..^trail.Length];
            var (kf, cf) = Classify(tok, true);
            var (kb, cb) = Classify(lead + core, true);
            if (trail == "" || core == "" || kf == Kind.Convert || (kf == Kind.Keep && kb != Kind.Convert))
                items.Add(new Item(kf, tok, cf, ""));
            else
                items.Add(new Item(kb, lead + core, cb, trail));
        }
        gaps.Add(t[pos..]);

        int strong = items.Count(i => i.Kind == Kind.Convert);
        int keep = items.Count(i => i.Kind == Kind.Keep);
        int guess = items.Count(i => i.Kind == Kind.Guess);
        if (strong == 0 && !(guess >= 2 && keep == 0)) return null;
        if (strong + guess < keep) return null;
        // Перевес «ошибочных» слов — переводим всё сообщение; ничья — только уверенно распознанные слова.
        var full = strong + guess > keep;

        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var gap = gaps[i];
            if (en && full) gap = gap.Replace('&', '?').Replace('/', '.');
            sb.Append(gap);
            var it = items[i];
            var conv = it.Kind == Kind.Convert || (full && it.Kind is Kind.Weak or Kind.Ambig or Kind.Guess);
            sb.Append(conv ? it.Conv : it.Orig).Append(it.Trail);
        }
        var last = gaps[^1];
        if (en && full) last = last.Replace('&', '?').Replace('/', '.');
        sb.Append(last);
        var res = sb.ToString();
        return res == t ? null : res;
    }
}
