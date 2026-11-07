using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Features;

public sealed class YandexException : Exception
{
    public YandexException(string message) : base(message) { }
}

public sealed record YandexTrack(string Id, string Title, int Duration, string Url, bool Available);

/// <summary>Яндекс Музыка: поиск и прямые ссылки на аудио через API api.music.yandex.net.</summary>
public sealed class YandexMusic
{
    public static readonly Regex TrackRe = new(@"^https?://music\.yandex\.(?:ru|com|by|kz|uz)/(?:album/\d+/)?track/(\d+)", RegexOptions.IgnoreCase);
    private const string Api = "https://api.music.yandex.net";
    private const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";

    private readonly Hub _hub;
    public string Status = "";

    public YandexMusic(Hub hub) { _hub = hub; }

    public string Token => _hub.S.Str("yandex.token").Trim();

    private async Task<JsonNode> Request(HttpMethod method, string path, HttpContent content = null)
    {
        if (Token == "") throw new YandexException("не указан токен Яндекс Музыки");
        using var req = new HttpRequestMessage(method, Api + path) { Content = content };
        req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Token);
        req.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
        using var resp = await _hub.Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
        {
            Status = "токен не принят";
            throw new YandexException("токен не принят (проверьте или обновите его)");
        }
        if (!resp.IsSuccessStatusCode)
            throw new YandexException($"ошибка {(int)resp.StatusCode}");
        Status = "подключено";
        return J.ParseLoose(text)?["result"];
    }

    private static YandexTrack Info(JsonNode t)
    {
        var artists = string.Join(", ", (t["artists"] as JsonArray ?? new JsonArray()).Select(a => a.Str("name")).Where(n => n != ""));
        var title = artists != "" ? $"{artists} — {t.Str("title")}" : t.Str("title");
        var id = t.Str("id");
        return new YandexTrack(id, title, (int)(t.Num("durationMs") / 1000), $"https://music.yandex.ru/track/{id}",
            t.Bool("available", true));
    }

    public async Task<YandexTrack> Track(string id)
    {
        var res = await Request(HttpMethod.Post, "/tracks",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["track-ids"] = id, ["with-positions"] = "False" }));
        var first = (res as JsonArray)?.FirstOrDefault();
        if (first == null) throw new YandexException("трек не найден");
        return Info(first);
    }

    public async Task<YandexTrack> Search(string query)
    {
        var res = await Request(HttpMethod.Get, "/search?type=track&page=0&nocorrect=False&text=" + Uri.EscapeDataString(query));
        var items = (res?.At("tracks.results") as JsonArray ?? new JsonArray())
            .Select(Info).Where(t => t.Available).ToList();
        if (items.Count == 0) throw new YandexException("ничего не найдено");
        return items[0];
    }

    public async Task<string> DirectLink(string id)
    {
        var infos = (await Request(HttpMethod.Get, $"/tracks/{id}/download-info") as JsonArray ?? new JsonArray())
            .OrderByDescending(i => i.Str("codec") == "mp3")
            .ThenByDescending(i => i.Int("bitrateInKbps"))
            .ToList();
        foreach (var info in infos)
        {
            var url = info.Str("downloadInfoUrl");
            if (url == "") continue;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Token);
            using var resp = await _hub.Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) continue;
            var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync());
            string Node(string name) => xml.Descendants(name).FirstOrDefault()?.Value ?? "";
            var host = Node("host");
            var path = Node("path");
            var ts = Node("ts");
            var s = Node("s");
            if (host == "" || path == "") continue;
            var sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(SignSalt + path[1..] + s))).ToLowerInvariant();
            return $"https://{host}/get-mp3/{sign}/{ts}{path}";
        }
        throw new YandexException("нет доступной ссылки (возможно, нужна подписка Плюс)");
    }

    public async Task<string> Check()
    {
        var res = await Request(HttpMethod.Get, "/account/status");
        var acc = res?["account"];
        var login = acc.Str("login");
        return login != "" ? login : acc.Str("displayName", "аккаунт");
    }
}
