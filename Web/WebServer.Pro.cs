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
            giveaway = P.Giveaway.State(),
            games = P.Games.State(),
            cloud = new { status = P.Cloud.Status, file = P.Cloud.Peek(), folders = CloudSync.DetectFolders() },
            profiles = Profiles.List(Config.Root, Config.ProfileName),
        })));

        // лёгкий опрос для открытой вкладки (без поиска папок и чтения копии)
        app.MapGet("/api/pro/live", H((c, b) => Ok(new
        {
            giveaway = P.Giveaway.State(),
            games = P.Games.State(),
        })));

        // свои темы оверлеев
        app.MapPost("/api/pro/themes", H((c, b) => Ok(P.SaveTheme(b))));
        app.MapDelete("/api/pro/themes/{id}", H((c, b) => { P.DeleteTheme(Route(c, "id")); return Ok(); }));

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
        nw.Remove("tts");   // озвучка удалена из программы
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
