using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Перевод через LibreTranslate — локальный сервер на моделях Argos Translate
/// (те же модели, что в Python-версии), либо совместимый сервис с ключом.
/// </summary>
public sealed class Translator
{
    private static readonly Regex UrlRe = new(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase);
    private static readonly Regex WordRe = new(@"[^\W\d_]{2,}");
    private readonly Hub _hub;
    public string Status = "";

    public Translator(Hub hub) { _hub = hub; }

    private JsonObject Cfg => _hub.S.Obj("tools.translate");
    public bool Enabled => Cfg.Bool("enabled");
    private string BaseUrl => Cfg.Str("url", "http://127.0.0.1:5000").Trim().TrimEnd('/');

    /// <summary>Грубое определение языка по алфавиту (латиница считается английским).</summary>
    public static string DetectLang(string text)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return "";
        double Share(Func<char, bool> pred) => letters.Count(pred) / (double)letters.Count;
        if (Share(c => c >= 'Ѐ' && c <= 'ӿ') > 0.5)
            return Regex.IsMatch(text, "[їієґ]", RegexOptions.IgnoreCase) ? "uk" : "ru";
        if (Share(c => c >= '぀' && c <= 'ヿ') > 0.2) return "ja";
        if (Share(c => c >= '가' && c <= '힯') > 0.5) return "ko";
        if (Share(c => c >= '一' && c <= '鿿') > 0.5) return "zh";
        if (Share(c => c >= '؀' && c <= 'ۿ') > 0.5) return "ar";
        if (Share(c => c < 128) > 0.5) return "en";
        if (Regex.IsMatch(text, "[äöüß]", RegexOptions.IgnoreCase)) return "de";
        if (Regex.IsMatch(text, "[ñ¿¡]", RegexOptions.IgnoreCase)) return "es";
        return "en";
    }

    public async Task<List<object>> Languages()
    {
        using var resp = await _hub.Http.GetAsync(BaseUrl + "/languages");
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"сервер перевода ответил {(int)resp.StatusCode}");
        Status = "подключено";
        return (JsonNode.Parse(text) as JsonArray ?? new JsonArray())
            .Select(l => (object)new { code = l.Str("code"), name = l.Str("name"), targets = l.StrList("targets") })
            .ToList();
    }

    /// <summary>Возвращает (перевод, исходный язык).</summary>
    public async Task<(string Text, string Source)> Translate(string text, string src = null, string dst = null)
    {
        src = (src ?? Cfg.Str("source", "auto")).Trim().ToLowerInvariant();
        dst = (dst ?? Cfg.Str("target", "ru")).Trim().ToLowerInvariant();
        if (src == "") src = "auto";
        if (src == "auto")
        {
            var guess = DetectLang(text);
            if (guess == dst) return (text, guess);
        }
        else if (src == dst) return (text, src);
        var body = new Dictionary<string, string> { ["q"] = text, ["source"] = src, ["target"] = dst, ["format"] = "text" };
        var key = Cfg.Str("api_key").Trim();
        if (key != "") body["api_key"] = key;
        HttpResponseMessage resp;
        try
        {
            resp = await _hub.Http.PostAsync(BaseUrl + "/translate",
                new StringContent(J.Serialize(body), Encoding.UTF8, "application/json"));
        }
        catch (Exception e)
        {
            Status = "нет связи";
            throw new InvalidOperationException($"сервер перевода недоступен ({BaseUrl}): {e.Message}");
        }
        using (resp)
        {
            var js = Twitch.TwitchClient.SafeParse(await resp.Content.ReadAsStringAsync());
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(js.Str("error", $"ошибка {(int)resp.StatusCode}"));
            Status = "подключено";
            var detected = js.Str("detectedLanguage.language", src == "auto" ? DetectLang(text) : src);
            return (js.Str("translatedText"), detected);
        }
    }

    public bool WantsAuto(string text)
    {
        if (!Cfg.Bool("enabled") || !Cfg.Bool("auto")) return false;
        var t = text.Trim();
        if (t == "" || t.StartsWith("!") || UrlRe.IsMatch(t)) return false;
        if (WordRe.Matches(t).Count < 2) return false;
        var src = Cfg.Str("source", "auto");
        var lang = DetectLang(t);
        if (lang == "" || lang == Cfg.Str("target", "ru")) return false;
        return src == "auto" || lang == src;
    }
}
