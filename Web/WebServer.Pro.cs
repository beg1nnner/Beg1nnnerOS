using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Features;
using Beg1nnnerOS.Pro;
using Beg1nnnerOS.Twitch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Beg1nnnerOS.Web;

/// <summary>API дополнительных функций.</summary>
public sealed partial class WebServer
{
    private ProHub P => _hub.Pro;

    private void MapPro(WebApplication app)
    {
        app.MapGet("/api/pro", H((c, b) => Ok(new
        {
            pro = P.State(),
            tts = P.Tts.State(),
            giveaway = P.Giveaway.State(),
            games = P.Games.State(),
            cloud = new { status = P.Cloud.Status, file = P.Cloud.Peek(), folders = CloudSync.DetectFolders() },
            profiles = Profiles.List(Config.Root, Config.ProfileName),
        })));

        // лёгкий опрос для открытой вкладки (без поиска папок и чтения копии)
        app.MapGet("/api/pro/live", H((c, b) => Ok(new
        {
            tts = new { status = P.Tts.Status, queue = P.Tts.QueueCount },
            giveaway = P.Giveaway.State(),
            games = P.Games.State(),
        })));

        // свои темы оверлеев
        app.MapPost("/api/pro/themes", H((c, b) => Ok(P.SaveTheme(b))));
        app.MapDelete("/api/pro/themes/{id}", H((c, b) => { P.DeleteTheme(Route(c, "id")); return Ok(); }));

        // озвучка
        app.MapPost("/api/pro/tts/test", H(async (c, b) =>
        {
            var text = P.Tts.Clean(b.Str("text") != "" ? b.Str("text") : "Проверка озвучки Beg1nnnerOS. Спасибо за донат!");
            try { await P.Tts.Speak(text); }
            catch (Exception e) when (e is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
            { throw new UserError("Озвучка не получилась: " + e.Message); }
            return Ok();
        }));
        app.MapPost("/api/pro/tts/skip", H((c, b) => { P.Tts.Skip(); return Ok(); }));
        app.MapPost("/api/pro/tts/clear", H((c, b) => { P.Tts.Clear(); return Ok(); }));

        // статистика стримов
        app.MapGet("/api/pro/streams", H((c, b) =>
        {
            var days = int.TryParse(c.Request.Query["days"].ToString(), out var d) ? Math.Max(1, Math.Min(3650, d)) : 30;
            return Ok(_hub.Stats.Streams(days));
        }));
        app.MapGet("/api/pro/streams/{id}", H((c, b) =>
        {
            return Ok(_hub.Stats.StreamDetails(Route(c, "id")) ?? throw new UserError("Стрим не найден"));
        }));

        // розыгрыш
        app.MapPost("/api/pro/giveaway/start", H(async (c, b) =>
        {
            await P.Giveaway.Start(b);
            // запоминаем поля формы для следующего раза
            var g = _hub.S.Obj("pro.giveaway");
            foreach (var k in new[] { "prize", "keyword", "reward_title", "minutes", "subs_only", "followers_only", "sub_luck", "winners" })
                if (b[k] != null) g[k] = b[k].DeepClone();
            _hub.Settings.Save();
            return Ok(P.Giveaway.State());
        }));
        app.MapPost("/api/pro/giveaway/close", H(async (c, b) => { await P.Giveaway.Close(); return Ok(P.Giveaway.State()); }));
        app.MapPost("/api/pro/giveaway/draw", H(async (c, b) =>
        {
            await P.Giveaway.Draw(b.Int("count", 0));
            return Ok(P.Giveaway.State());
        }));
        app.MapPost("/api/pro/giveaway/reroll", H(async (c, b) =>
        {
            await P.Giveaway.Reroll(b.Str("login"));
            return Ok(P.Giveaway.State());
        }));
        app.MapPost("/api/pro/giveaway/finish", H((c, b) => { P.Giveaway.Finish(); return Ok(P.Giveaway.State()); }));

        // мини-игры
        app.MapPost("/api/pro/games/guess", H(async (c, b) =>
        {
            await P.Games.StartGuess(b.Int("max", 100));
            return Ok(P.Games.State());
        }));
        app.MapPost("/api/pro/games/guess/stop", H(async (c, b) => { await P.Games.StopGuess(); return Ok(P.Games.State()); }));

        // облачная копия
        app.MapPost("/api/pro/cloud/save", H((c, b) => Ok(P.Cloud.Save(true))));
        app.MapPost("/api/pro/cloud/restore", H((c, b) => Ok(P.Cloud.Restore(b.Str("password")))));

        // профили
        app.MapPost("/api/pro/profiles", H((c, b) =>
        {
            var id = Profiles.Create(Config.Root, b.Str("name"), b.Bool("copy") ? Config.Profile : null);
            _hub.Log.Add("system", $"Создан профиль «{id}»");
            return Ok(Profiles.List(Config.Root, Config.ProfileName));
        }));
        app.MapPost("/api/pro/profiles/switch", H((c, b) =>
        {
            var id = Profiles.Clean(b.Str("name"));
            if (id == Config.ProfileName) throw new UserError("Этот профиль уже открыт");
            Profiles.SetActive(Config.Root, id);
            _hub.Log.Add("system", $"Переключение на профиль «{(id == "" ? Profiles.MainTitle : id)}» — перезапуск");
            _ = Task.Delay(600).ContinueWith(_ => _hub.RestartApp());
            return Ok();
        }));
        app.MapDelete("/api/pro/profiles/{name}", H((c, b) =>
        {
            Profiles.Delete(Config.Root, Uri.UnescapeDataString(Route(c, "name")), Config.ProfileName);
            return Ok(Profiles.List(Config.Root, Config.ProfileName));
        }));

        app.MapPost("/api/pro/restart", H((c, b) =>
        {
            _ = Task.Delay(400).ContinueWith(_ => _hub.RestartApp());
            return Ok();
        }));
    }

    /// <summary>Проверка раздела «pro» в настройках; темы управляются отдельно и здесь не перезаписываются.</summary>
    private static void ValidatePro(JsonObject nw, JsonObject incoming, JsonObject old)
    {
        nw["themes"] = old?["themes"]?.DeepClone() ?? new JsonArray();
        var tts = nw.Obj("tts");
        if (tts != null)
        {
            tts.Remove("has_yandex_key");
            if (incoming.Str("tts.yandex_key").Trim() == "") tts["yandex_key"] = old.Str("tts.yandex_key");
            tts["yandex_key"] = tts.Str("yandex_key").Trim();
            if (tts.Str("engine") is not ("windows" or "yandex")) tts["engine"] = "windows";
            if (!Tts.YandexVoices.Contains(tts.Str("yandex_voice"))) tts["yandex_voice"] = "alena";
            tts["rate"] = Math.Max(-10, Math.Min(10, tts.Int("rate")));
            tts["volume"] = Math.Max(0, Math.Min(100, tts.Int("volume", 80)));
            tts["yandex_speed"] = Math.Max(0.5, Math.Min(2.0, tts.Num("yandex_speed", 1.0)));
            tts["min_donation"] = Math.Max(0, tts.Num("min_donation"));
            tts["min_bits"] = Math.Max(1, tts.Int("min_bits", 100));
            tts["max_length"] = Math.Max(20, Math.Min(1000, tts.Int("max_length", 200)));
            tts["command_cooldown"] = Math.Max(0, Math.Min(3600, tts.Int("command_cooldown", 30)));
            if (!Commands.Access.ContainsKey(tts.Str("command_access"))) tts["command_access"] = "vip";
            if (tts.Str("reward_title").Length > 45) throw new UserError("Название награды для озвучки — не длиннее 45 символов", "pro.tts.reward_title");
            if (tts["banned_words"] is JsonValue)
            {
                var arr = new JsonArray();
                foreach (var w in tts.Str("banned_words").Split('\n').Select(x => x.Trim()).Where(x => x != "")) arr.Add(w);
                tts["banned_words"] = arr;
            }
        }
        var cloud = nw.Obj("cloud");
        if (cloud != null)
        {
            cloud.Remove("has_password");
            if (incoming.Str("cloud.password") == "") cloud["password"] = old.Str("cloud.password");
            cloud["folder"] = cloud.Str("folder").Trim().Trim('"');
            if (cloud.Bool("include_secrets") && cloud.Str("password").Length < 6)
                throw new UserError("Для копии с ключами и входами задайте пароль не короче 6 символов", "pro.cloud.password");
        }
        var games = nw.Obj("games");
        if (games != null)
        {
            games["cooldown"] = Math.Max(0, Math.Min(3600, games.Int("cooldown", 30)));
            games["duel_timeout"] = Math.Max(15, Math.Min(300, games.Int("duel_timeout", 60)));
            games["guess_max"] = Math.Max(10, Math.Min(1_000_000, games.Int("guess_max", 100)));
        }
    }
}
