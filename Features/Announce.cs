using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Публикации вне Twitch: анонс начала стрима, итоги стрима и клипы — в Telegram (бот) и Discord (вебхук).
/// Настройки — раздел «announce».
/// </summary>
public sealed class Announce
{
    public const string DefaultText = "🔴 {channel} в эфире!\n\n{title}\n🎮 {category}\n\n👉 {link}";

    private readonly Hub _hub;
    public Announce(Hub hub) => _hub = hub;
    private JsonObject Cfg => _hub.S.Obj("announce");

    // ---------- начало стрима ----------
    /// <summary>Вызывается один раз на стрим (не при перезапуске программы посреди эфира).</summary>
    public async Task OnLive(string streamId)
    {
        var c = Cfg;
        if (!c.Bool("tg_enabled") && !c.Bool("dc_enabled")) return;
        var st = _hub.State;
        if (st.Data.Str("announce_stream") == streamId) return;
        st.Data["announce_stream"] = streamId;
        st.Save();
        // Даём Twitch минуту: появится превью, а название и категория успеют обновиться
        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(c.Int("delay", 60), 0, 600)));
        if (!_hub.Stream.Live || _hub.Stream.StreamId != streamId) return;
        try { await _hub.Stream.Refresh(); } catch { }
        var (text, photo, link) = await LiveMessage();
        await Send(text, photo, link, "Анонс стрима", "live");
    }

    private async Task<(string Text, string Photo, string Link)> LiveMessage(bool test = false)
    {
        var c = Cfg;
        var login = _hub.Twitch.Streamer.Login;
        var name = login;
        var link = $"https://twitch.tv/{login}";
        var tpl = c.Str("text").Trim();
        if (tpl == "") tpl = DefaultText;
        var text = tpl.Replace("{channel}", name).Replace("{title}", _hub.Stream.Title)
                      .Replace("{category}", _hub.Stream.Category == "" ? "—" : _hub.Stream.Category).Replace("{link}", link);
        if (test) text = "🧪 Проверка анонса (так будет выглядеть пост при начале стрима)\n\n" + text;
        string photo = "";
        if (c.Bool("photo", true))
        {
            try
            {
                var s = await _hub.Twitch.GetStream();
                var thumb = s?.Str("thumbnail_url") ?? "";
                if (thumb != "")
                    photo = thumb.Replace("{width}", "1280").Replace("{height}", "720") + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                else
                {
                    // не в эфире (проверка) — заставка канала, если она есть
                    var u = await _hub.Twitch.Api(HttpMethod.Get, "/users", new() { ["id"] = _hub.Twitch.BroadcasterId });
                    photo = u?["data"]?[0]?.Str("offline_image_url") ?? "";
                }
            }
            catch { }
        }
        return (text, photo, link);
    }

    public async Task<string> Test(string target)
    {
        if (!_hub.Twitch.Streamer.Ready) throw new UserError("Сначала войдите в Twitch");
        var (text, photo, link) = await LiveMessage(test: true);
        return target == "discord" ? await Discord(text, photo, link, true) : await Telegram(text, photo, true);
    }

    // ---------- итоги стрима ----------
    /// <summary>После окончания эфира: ждём 2 минуты (догоняют события), затем публикуем итоги, если включено.</summary>
    public async Task OnOffline(string streamId)
    {
        if (string.IsNullOrEmpty(streamId) || !Cfg.Bool("summary_auto")) return;
        if (!Cfg.Bool("tg_enabled") && !Cfg.Bool("dc_enabled")) return;
        var st = _hub.State;
        if (st.Data.Str("summary_stream") == streamId) return;   // и событие, и проверка раз в минуту — отправляем один раз
        st.Data["summary_stream"] = streamId;
        st.Save();
        await Task.Delay(TimeSpan.FromMinutes(2));
        if (_hub.Stream.Live && _hub.Stream.StreamId == streamId) return;   // стрим «воскрес» после обрыва
        var text = Summary(streamId);
        if (text == null) return;
        await Send(text, "", "", "Итоги стрима", "summary");
    }

    /// <summary>Текст итогов стрима или null, если про стрим нет данных.</summary>
    public string Summary(string streamId)
    {
        var d = _hub.Stats.SummaryData(streamId);
        if (d == null) return null;
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        string N(long v) => v.ToString("N0", ru);
        var sb = new StringBuilder();
        var title = d.Str("title");
        sb.AppendLine($"📊 Итоги стрима{(title != "" ? $" «{title}»" : "")}");
        sb.AppendLine();
        var dur = TimeSpan.FromSeconds(d.Num("duration"));
        var cat = d.Str("category");
        sb.AppendLine($"⏱ {(int)dur.TotalHours} ч {dur.Minutes} мин{(cat != "" ? $" · 🎮 {cat}" : "")}");
        if (d.Num("peak") > 0) sb.AppendLine($"👀 Пик зрителей: {N((long)d.Num("peak"))} · в среднем {N((long)d.Num("avg"))}");
        if (d.Num("messages") > 0) sb.AppendLine($"💬 Сообщений: {N((long)d.Num("messages"))} от {N((long)d.Num("chatters"))} зрителей");
        var parts = new List<string>();
        if (d.Num("follows") > 0) parts.Add($"новых фолловеров: {N((long)d.Num("follows"))}");
        if (d.Num("subs") > 0) parts.Add($"сабов: {N((long)d.Num("subs"))}");
        if (d.Num("bits") > 0) parts.Add($"битсов: {N((long)d.Num("bits"))}");
        if (d.Num("raids") > 0) parts.Add($"рейдов: {N((long)d.Num("raids"))}");
        if (parts.Count > 0) sb.AppendLine("💜 " + char.ToUpper(parts[0][0]) + string.Join(" · ", parts)[1..]);
        var don = d["donations"] as JsonObject;
        if (don != null && don.Count > 0)
            sb.AppendLine("💸 Донаты: " + string.Join(", ", don.Select(p => $"{J.AsNum(p.Value):N0} {Cur(p.Key)}".Trim())));
        if (d.Num("orders") > 0) sb.AppendLine($"🎵 Заказано треков: {N((long)d.Num("orders"))}");
        var chat = d["top_chat"] as JsonArray;
        if (chat != null && chat.Count > 0)
            sb.AppendLine("🏆 Самые активные: " + string.Join(", ", chat.Take(3).Select(x => $"{x?[0]} ({x?[1]})")));
        var tracks = d["top_tracks"] as JsonArray;
        if (tracks != null && tracks.Count > 0)
        {
            sb.AppendLine("🎶 Треки стрима:");
            var i = 0;
            foreach (var t in tracks.Take(5)) sb.AppendLine($"  {++i}. {t}");
        }
        sb.AppendLine();
        sb.Append("Спасибо всем, кто был на стриме! ❤️");
        return sb.ToString();
    }

    private static string Cur(string c) => c switch { "RUB" => "₽", "USD" => "$", "EUR" => "€", "UAH" => "₴", "KZT" => "₸", _ => c };

    public async Task<string> SendSummary(string streamId, string target)
    {
        var text = Summary(streamId) ?? throw new UserError("По этому стриму нет данных");
        return target == "discord" ? await Discord(text, "", "", true) : await Telegram(text, "", true);
    }

    // ---------- клипы ----------
    public async Task OnClip(string url, string title, string author)
    {
        if (!Cfg.Bool("clips_post")) return;
        var text = $"🎬 Новый клип{(title != "" ? $": {title}" : "")}\nСоздал: {author}\n\n{url}";
        await Send(text, "", url, "Клип", "clip");
    }

    // ---------- отправка ----------
    private async Task Send(string text, string photo, string link, string what, string kind)
    {
        text = L.T(text);
        var c = Cfg;
        if (c.Bool("tg_enabled"))
        {
            try { await Telegram(text, photo, false); _hub.Log.Add("system", $"{what} опубликован в Telegram"); }
            catch (Exception e) { _hub.Log.Error($"{what} в Telegram: {e.Message}"); }
        }
        if (c.Bool("dc_enabled"))
        {
            try { await Discord(text, photo, link, false); _hub.Log.Add("system", $"{what} опубликован в Discord"); }
            catch (Exception e) { _hub.Log.Error($"{what} в Discord: {e.Message}"); }
        }
    }

    private async Task<string> Telegram(string text, string photo, bool test)
    {
        var c = Cfg;
        var token = c.Str("tg_token").Trim();
        var chat = c.Str("tg_chat").Trim();
        if (token == "" || chat == "") throw new UserError("Укажите токен бота и канал Telegram");
        if (chat.StartsWith("https://t.me/")) chat = "@" + chat[13..];
        if (!chat.StartsWith("@") && !chat.StartsWith("-") && !long.TryParse(chat, out _)) chat = "@" + chat;
        var api = $"https://api.telegram.org/bot{token}/";
        // подпись к фото — не длиннее 1024 символов; длинный текст отправляем обычным сообщением
        var asPhoto = photo != "" && text.Length <= 1024;
        var form = new Dictionary<string, string> { ["chat_id"] = chat };
        if (asPhoto) { form["photo"] = photo; form["caption"] = text; }
        else { form["text"] = text; form["disable_web_page_preview"] = "false"; }
        var res = await Post(api + (asPhoto ? "sendPhoto" : "sendMessage"), new FormUrlEncodedContent(form), "Telegram");
        var js = J.ParseLoose(res.Body);
        if (!js.Bool("ok") && asPhoto)
        {
            // Telegram не смог забрать картинку — отправляем текстом
            form.Remove("photo"); form.Remove("caption"); form["text"] = text;
            res = await Post(api + "sendMessage", new FormUrlEncodedContent(form), "Telegram");
            js = J.ParseLoose(res.Body);
        }
        if (!js.Bool("ok")) throw new UserError("Telegram: " + TgError(js.Str("description")));
        return "Сообщение отправлено в Telegram";
    }

    private static string TgError(string d)
    {
        var l = d.ToLowerInvariant();
        if (l.Contains("unauthorized")) return "неверный токен бота";
        if (l.Contains("chat not found")) return "канал не найден — проверьте @адрес и что бот добавлен в канал";
        if (l.Contains("not enough rights") || l.Contains("have no rights") || l.Contains("not a member"))
            return "у бота нет прав — добавьте его в администраторы канала с правом публикации";
        return d;
    }

    private async Task<string> Discord(string text, string photo, string link, bool test)
    {
        var hook = Cfg.Str("dc_webhook").Trim();
        if (!hook.StartsWith("https://discord.com/api/webhooks/") && !hook.StartsWith("https://discordapp.com/api/webhooks/"))
            throw new UserError("Укажите адрес вебхука Discord (https://discord.com/api/webhooks/…)");
        var lines = text.Split('\n');
        var embed = new JsonObject
        {
            ["description"] = text.Length > 4000 ? text[..4000] : text,
            ["color"] = 0x9146FF,
        };
        if (link != "") embed["url"] = link;
        if (link != "" && lines.Length > 0) embed["title"] = lines[0].Length > 250 ? lines[0][..250] : lines[0];
        if (embed["title"] != null) embed["description"] = string.Join("\n", lines.Skip(1)).Trim();
        if (photo != "") embed["image"] = new JsonObject { ["url"] = photo };
        var body = new JsonObject
        {
            ["username"] = "Beg1nnnerOS",
            ["content"] = link != "" ? link : null,
            ["embeds"] = new JsonArray(embed),
        };
        var res = await Post(hook + (hook.Contains('?') ? "&" : "?") + "wait=true",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), "Discord");
        if (res.Status is >= 200 and < 300) return "Сообщение отправлено в Discord";
        var js = J.ParseLoose(res.Body);
        throw new UserError("Discord: " + (res.Status == 404 || res.Status == 401 ? "вебхук не найден — создайте новый" : js?.Str("message") ?? res.Status.ToString()));
    }

    private async Task<(int Status, string Body)> Post(string url, HttpContent content, string service)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await _hub.Http.PostAsync(url, content, cts.Token);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new UserError($"нет связи с {service} — сервис может быть заблокирован у провайдера (нужен VPN или zapret)");
        }
    }
}
