using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Desktop;
using Beg1nnnerOS.Features;
using Beg1nnnerOS.Twitch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Beg1nnnerOS.Web;

/// <summary>HTTP-сервер: панель, OBS-страницы (/, /alerts, /dock), API и OAuth-callback (порт 4343).</summary>
public sealed partial class WebServer
{
    private readonly Hub _hub;
    private WebApplication _app;
    public Action ShowWindow = () => { };
    public int Port { get; private set; }

    private static readonly (string Sec, string Key)[] Secrets =
        { ("twitch", "client_secret"), ("yandex", "token"), ("donationalerts", "token"),
          ("donationalerts", "client_secret"), ("donationalerts", "refresh_token"),
          ("announce", "tg_token"), ("announce", "dc_webhook") };
    private static readonly string[] Sections =
        { "server", "twitch", "chat", "autopost", "music", "overlay", "alerts", "events", "chat_overlay", "yandex", "donationalerts", "tools", "hotkeys", "ui", "pro", "announce", "votes", "mod" };

    public WebServer(Hub hub) { _hub = hub; }

    // ---------- ответы ----------
    private static IResult Json(object obj, int status = 200) =>
        Results.Text(J.Serialize(obj), "application/json; charset=utf-8", Encoding.UTF8, status);

    private static IResult Ok(object data = null, Dictionary<string, object> extra = null)
    {
        var o = new JsonObject { ["ok"] = true };
        if (data != null) o["data"] = J.From(data);
        if (extra != null) foreach (var kv in extra) o[kv.Key] = J.From(kv.Value);
        return Results.Text(o.ToJsonString(J.Options), "application/json; charset=utf-8", Encoding.UTF8);
    }

    private static IResult Fail(string msg, int status = 400, string field = null) =>
        field == null ? Json(new { ok = false, error = msg }, status) : Json(new { ok = false, error = msg, field }, status);

    // Названия настроек так, как они подписаны в интерфейсе
    private static readonly (string Key, string Label, long Lo, long Hi)[] MusicLimits =
    {
        ("per_user_limit", "Лимит заказов от зрителя", 1, 100),
        ("queue_limit", "Длина очереди", 1, 500),
        ("max_duration_min", "Макс. длительность трека, мин", 1, 180),
        ("skip_votes", "Голосов для !skip", 1, 100),
        ("volume", "Громкость музыки", 0, 100),
        ("min_views", "Мин. просмотров на YouTube", 0, 1_000_000_000),
    };
    private static readonly Dictionary<string, string> RewardNames = new()
    {
        ["order"] = "Заказать музыку", ["priority"] = "Трек вне очереди", ["skip"] = "Пропустить трек",
    };

    private static string Fmt(long n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Целое число из поля (null, если там не целое число).</summary>
    private static long? WholeNumber(JsonNode n)
    {
        if (n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            var d = J.AsNum(n);
            return d == Math.Floor(d) ? (long)d : null;
        }
        var text = J.AsStr(n).Replace(" ", "").Replace(",", ".");
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
               && x == Math.Floor(x) ? (long)x : null;
    }

    private static UserError RangeError(string field, string label, long lo, long hi, JsonNode value)
    {
        var shown = J.AsStr(value) == "" ? "" : $" (сейчас: {J.AsStr(value)})";
        return new UserError($"«{label}»: укажите целое число от {Fmt(lo)} до {Fmt(hi)}{shown}", field);
    }

    private static async Task<JsonNode> Body(HttpContext ctx)
    {
        try
        {
            using var r = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var text = await r.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    /// <summary>Обёртка: тело запроса читается в потоке запроса, логика выполняется в HubLoop.</summary>
    private Delegate H(Func<HttpContext, JsonNode, Task<IResult>> handler) => async (HttpContext ctx) =>
    {
        var body = ctx.Request.Method is "POST" or "PUT" or "PATCH" ? await Body(ctx) : new JsonObject();
        try
        {
            return await _hub.Loop.Call(() => handler(ctx, body));
        }
        catch (UserError e) { return Fail(e.Message, 400, e.Field); }
        catch (Exception e)
        {
            _hub.Log.Error($"API {ctx.Request.Path}: {e.GetType().Name}: {e.Message}");
            return Fail($"Не удалось выполнить действие: {e.Message}. Подробности — в журнале на вкладке «Эфир»", 500);
        }
    };

    private Delegate H(Func<HttpContext, JsonNode, IResult> handler) => H((ctx, b) => Task.FromResult(handler(ctx, b)));

    /// <summary>Явный Delegate, чтобы ASP.NET не выбрал перегрузку RequestDelegate и не потерял IResult.</summary>
    private static Delegate D(Func<HttpContext, Task<IResult>> f) => f;

    private static string Route(HttpContext ctx, string name) => ctx.Request.RouteValues[name]?.ToString() ?? "";

    // ---------- запуск ----------
    public async Task Start()
    {
        var host = _hub.S.Str("server.host", "127.0.0.1");
        Port = _hub.S.Int("server.port", 8080);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{Port}", $"http://localhost:{Config.CallbackPort}");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 60L * 1024 * 1024);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 60L * 1024 * 1024);
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Config.WebDir), RequestPath = "/static",
            OnPrepareResponse = c => c.Context.Response.Headers["Cache-Control"] = "no-store",
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(Config.MediaDir), RequestPath = "/media",
            ServeUnknownFileTypes = false,
        });
        Map(app);
        _app = app;
        await app.StartAsync();
    }

    public async Task Stop()
    {
        if (_app == null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(cts.Token);
    }

    private static IResult Page(string name)
    {
        var path = Path.Combine(Config.WebDir, name);
        return Results.File(path, "text/html; charset=utf-8");
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/", (HttpContext c) => IsCallbackPort(c) ? Results.NotFound() : NoStore(c, Page("overlay.html")));
        app.MapGet("/alerts", (HttpContext c) => NoStore(c, Page("alerts.html")));
        app.MapGet("/dock", (HttpContext c) => NoStore(c, Page("dock.html")));
        app.MapGet("/panel", (HttpContext c) => NoStore(c, Page("panel.html")));
        app.MapGet("/chat", (HttpContext c) => NoStore(c, Page("chat.html")));
        app.MapGet("/music-obs", (HttpContext c) => NoStore(c, Page("music-obs.html")));
        app.MapGet("/votes", (HttpContext c) => NoStore(c, Page("votes.html")));
        app.MapGet("/ws/chat", (RequestDelegate)WsChat);
        app.MapGet("/api/chat/overlay", H((c, b) => Ok(_hub.ChatOverlay.Config())));
        app.MapGet("/callback", D(Callback));
        app.MapGet("/da", D(DaCallback));
        app.MapGet("/ws/alerts", (RequestDelegate)WsAlerts);
        MapPro(app);

        // состояние и журнал
        app.MapGet("/api/state", H((c, b) => Ok(new
        {
            version = Config.Version, ready = _hub.Readiness(), music = _hub.Music.State(),
            next_timer = _hub.Timers.NextInfo(), paused_events = _hub.Events.Paused,
            alerts = new { pending = _hub.Alerts.Pending.Count, current = _hub.Alerts.Current, volume = _hub.S.Int("alerts.volume", 80) },
            port = Port, ui_theme = _hub.S.Str("ui.theme", "twitch"),
            update = _hub.Updater.State(),
        })));

        // обновления программы (GitHub Releases)
        app.MapPost("/api/update/check", H(async (c, b) =>
        {
            try { await _hub.Updater.Check(); } catch { }
            return Ok(_hub.Updater.State());
        }));
        app.MapPost("/api/update/install", H((c, b) =>
        {
            _hub.Updater.EnsureCanInstall();
            _hub.Loop.Start(_hub.Updater.Install);   // скачивание идёт в фоне, панель показывает прогресс
            return Ok(_hub.Updater.State());
        }));
        app.MapPost("/api/update/skip", H((c, b) =>
        {
            _hub.S.Set("update.skip", b.Str("version"));
            _hub.Settings.Save();
            return Ok(_hub.Updater.State());
        }));
        app.MapPost("/api/update/auto", H((c, b) =>
        {
            _hub.S.Set("update.auto_check", b.Bool("on"));
            _hub.Settings.Save();
            return Ok(_hub.Updater.State());
        }));
        app.MapGet("/api/log", H((c, b) => Ok(_hub.Log.Tail(150, c.Request.Query["kind"].ToString()))));
        app.MapPost("/api/log/clear", H((c, b) => { _hub.Log.Clear(); return Ok(); }));
        app.MapPost("/api/app/show", H((c, b) => { ShowWindow(); return Ok(); }));

        // настройки
        app.MapGet("/api/settings", H((c, b) => GetSettings()));
        app.MapGet("/api/lang", (HttpContext c) => NoStore(c, Json(new { ok = true, data = new { lang = L.Lang } })));
        app.MapPost("/api/settings", H((c, b) => SaveSettings(b)));

        // Twitch
        app.MapPost("/api/auth/{kind}", H((c, b) =>
        {
            var kind = Route(c, "kind");
            if (kind == "da")
            {
                var daUrl = _hub.Donations.BuildAuthUrl();
                Desktop.Shell.OpenUrl(daUrl);
                return Ok(null, new() { ["url"] = daUrl, ["opened"] = true });
            }
            if (kind != "streamer" && kind != "bot") throw new UserError("Неизвестный вход");
            if (kind == "bot" && _hub.S.Str("twitch.bot_login") == "") throw new UserError("Сначала укажите и сохраните ник аккаунта бота");
            var url = _hub.Twitch.BuildAuthUrl(kind);
            Desktop.Shell.OpenUrl(url);   // вход — в системном браузере, где уже открыт Twitch
            return Ok(null, new() { ["url"] = url, ["opened"] = true });
        }));
        // вход по коду (без своих ключей)
        app.MapPost("/api/twitch/device/{kind}", H(async (c, b) =>
        {
            var kind = Route(c, "kind");
            if (kind != "streamer" && kind != "bot") throw new UserError("Неизвестный вход");
            var d = await _hub.Twitch.StartDevice(kind);
            if (b.Bool("open", true)) Desktop.Shell.OpenUrl(d.Uri);
            return Ok(_hub.Twitch.DeviceState());
        }));
        app.MapGet("/api/twitch/device", H((c, b) => Ok(_hub.Twitch.DeviceState())));
        app.MapPost("/api/twitch/device-cancel", H((c, b) => { _hub.Twitch.CancelDevice(); return Ok(_hub.Twitch.DeviceState()); }));

        app.MapPost("/api/logout/{kind}", H((c, b) =>
        {
            if (Route(c, "kind") == "da") { _hub.Donations.Logout(); return Ok(); }
            (Route(c, "kind") == "streamer" ? _hub.Twitch.Streamer : _hub.Twitch.Bot).Logout();
            _hub.EventSub.Restart();
            return Ok();
        }));
        app.MapPost("/api/chat/send", H(async (c, b) =>
        {
            if (!await _hub.Twitch.SendChat(b.Str("text")))
                throw new UserError(_hub.Twitch.LastSendError != "" ? _hub.Twitch.LastSendError : "Сообщение не отправлено (проверьте отправителя)");
            return Ok();
        }));
        app.MapPost("/api/rewards/sync", H(async (c, b) =>
        {
            await _hub.Rewards.Sync();
            return Ok(null, new() { ["status"] = _hub.Rewards.Status });
        }));

        // команды
        var cmds = _hub.Commands;
        app.MapGet("/api/commands", H((c, b) => Ok(new { custom = cmds.Custom, standard = cmds.Standard })));
        app.MapPost("/api/commands/custom", H((c, b) => Ok(cmds.SaveCustom(b))));
        app.MapDelete("/api/commands/custom/{id}", H((c, b) => { cmds.DeleteCustom(Route(c, "id")); return Ok(); }));
        app.MapPost("/api/commands/standard/{key}", H((c, b) => Ok(cmds.SaveStandard(Route(c, "key"), b))));
        app.MapDelete("/api/commands/standard/{key}", H((c, b) => { cmds.DeleteStandard(Route(c, "key")); return Ok(); }));
        app.MapPost("/api/commands/import/fetch", H(async (c, b) =>
            Ok(b.Str("source") == "text" ? _hub.CommandImport.FromText(b.Str("text")) : await _hub.CommandImport.Fetch(b.Str("source"), b.Str("channel")))));
        app.MapPost("/api/commands/import", H((c, b) => Ok(_hub.CommandImport.Import(b["items"]))));
        app.MapPost("/api/commands/reset", H((c, b) => { cmds.ResetStandard(); return Ok(); }));
        app.MapPost("/api/commands/toggle", H((c, b) => { cmds.SetEnabled(b.Str("kind"), b.Str("id"), b.Bool("enabled")); return Ok(); }));
        app.MapPost("/api/commands/preview", H((c, b) =>
            Ok(cmds.Preview(b.Str("template"), b.Str("user"), b.Str("args"), b.Int("random_max", 100)))));

        // таймеры
        app.MapGet("/api/timers", H((c, b) => Ok(new { repeating = _hub.Timers.Repeating, viewer = _hub.Timers.Viewer, next = _hub.Timers.NextInfo() })));
        app.MapPost("/api/timers", H((c, b) => Ok(_hub.Timers.SaveRepeating(b))));
        app.MapDelete("/api/timers/{id}", H((c, b) => { _hub.Timers.DeleteRepeating(Route(c, "id")); return Ok(); }));
        app.MapDelete("/api/viewer_timers/{id}", H((c, b) => { _hub.Timers.Cancel(Route(c, "id")); return Ok(); }));

        // события
        app.MapPost("/api/events/preview", H((c, b) => Ok(_hub.Events.Preview(b.Str("type"), b.Str("text")))));
        app.MapPost("/api/events/pause", H((c, b) =>
        {
            _hub.Events.SetPaused(!_hub.Events.Paused);
            return Ok(null, new() { ["paused"] = _hub.Events.Paused });
        }));

        // статистика
        app.MapGet("/api/stats", H((c, b) =>
        {
            var period = c.Request.Query["period"].ToString();
            if (period == "") period = "stream";
            return Ok(new { chat = _hub.Stats.ChatSummary(period, _hub.Stream.Key), music = _hub.Stats.MusicSummary(period, _hub.Stream.Key) });
        }));

        // музыка
        // анонсы и итоги стрима в Telegram / Discord
        app.MapPost("/api/announce/test", H(async (c, b) =>
            Ok(new { message = await _hub.Announce.Test(b.Str("target") == "discord" ? "discord" : "telegram") })));
        app.MapGet("/api/stream-summary/{id}", H((c, b) =>
            Ok(new { text = _hub.Announce.Summary(Route(c, "id")) ?? throw new UserError("По этому стриму нет данных") })));
        app.MapPost("/api/stream-summary/{id}/send", H(async (c, b) =>
            Ok(new { message = await _hub.Announce.SendSummary(Route(c, "id"), b.Str("target") == "discord" ? "discord" : "telegram") })));

        // фильтр спама и ссылок
        app.MapGet("/api/mod", H((c, b) => Ok(_hub.Moderation.State())));
        app.MapPost("/api/mod/test", H((c, b) => Ok(_hub.Moderation.Test(b.Str("text")))));
        app.MapPost("/api/mod/unban", H(async (c, b) => Ok(new { message = await _hub.Moderation.Unban(b.Str("user_id")) })));

        // опросы и прогнозы Twitch
        app.MapGet("/api/votes", H((c, b) => Ok(_hub.Votes.State())));
        app.MapPost("/api/votes/refresh", H(async (c, b) => { await _hub.Votes.Refresh(); return Ok(_hub.Votes.State()); }));
        app.MapPost("/api/votes/poll/start", H(async (c, b) => Ok(new { message = await _hub.Votes.StartPoll(b) })));
        app.MapPost("/api/votes/poll/end", H(async (c, b) => Ok(new { message = await _hub.Votes.EndPoll(b.Bool("archive")) })));
        app.MapPost("/api/votes/prediction/start", H(async (c, b) => Ok(new { message = await _hub.Votes.StartPrediction(b) })));
        app.MapPost("/api/votes/prediction/{action}", H(async (c, b) =>
            Ok(new { message = await _hub.Votes.UpdatePrediction(Route(c, "action"), b.Str("outcome_id")) })));
        app.MapPost("/api/votes/templates", H((c, b) => { _hub.Votes.SaveTemplates(b.Str("kind") == "poll" ? "poll" : "prediction", b["list"]); return Ok(); }));

        // история сыгранных треков
        app.MapGet("/api/music/history", H((c, b) =>
        {
            var q = c.Request.Query;
            var scope = (string)q["scope"] ?? "stream";
            if (scope is not ("stream" or "today" or "week" or "all")) scope = "stream";
            int.TryParse(q["offset"], out var offset);
            int.TryParse(q["limit"], out var limit);
            return Ok(_hub.Stats.History(scope, _hub.Stream.Key, (string)q["q"] ?? "", q["fallback"] != "0",
                limit <= 0 ? 50 : limit, offset));
        }));

        // OBS-плеер: страница сообщает позицию/окончание и узнаёт, что играть
        app.MapGet("/api/music/obs", H((c, b) =>
        {
            var q = c.Request.Query;
            int.TryParse(q["gen"], out var gen);
            int.TryParse(q["ended"], out var ended);
            double.TryParse(q["pos"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pos);
            var err = (string)q["err"] ?? "";
            return Ok(_hub.Music.ObsPoll(gen, pos, ended, err.Length > 200 ? err[..200] : err));
        }));
        // файл текущего трека для OBS-плеера (с поддержкой перемотки по Range)
        app.MapGet("/api/music/audio/{name}", async (HttpContext c, string name) =>
        {
            var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var gen)) return Results.NotFound();
            var path = await _hub.Loop.CallSync(() => _hub.Music.ObsFile(gen));
            if (path == null || !File.Exists(path)) return Results.NotFound();
            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".m4a" or ".mp4" => "audio/mp4",
                ".mp3" => "audio/mpeg",
                ".webm" => "audio/webm",
                ".opus" or ".ogg" => "audio/ogg",
                _ => "application/octet-stream",
            };
            c.Response.Headers["Cache-Control"] = "no-store";
            return Results.File(path, mime, enableRangeProcessing: true);
        });

        app.MapGet("/api/now", H((c, b) =>
        {
            if (c.Request.Query["obs"] == "1") _hub.PlayerOverlaySeen = DateTime.UtcNow;
            var st = J.From(_hub.Music.State()).AsObject();
            st["overlay"] = _hub.S["overlay"].DeepClone();
            st["custom_themes"] = _hub.Pro.ThemesFor("player");
            return Ok(st);
        }));
        app.MapPost("/api/music/add", H(async (c, b) =>
        {
            var (good, text, t) = await _hub.Music.Request(b.Str("query"), "", "", manual: true);
            if (!good) throw new UserError(text);
            _hub.Stats.Music(_hub.Stream.Key, "manual", "ok", "", "Стример", t.Title);
            return Ok(null, new() { ["message"] = text });
        }));
        app.MapPost("/api/music/{action}", H(async (c, b) => await MusicAction(Route(c, "action"), b)));

        // МедиаАлерты
        var alerts = _hub.Alerts;
        app.MapGet("/api/alerts", H((c, b) => Ok(new { items = alerts.Items, pending = alerts.Pending, clients = alerts.Clients.Count })));
        app.MapPost("/api/alerts/upload", D(Upload));
        app.MapPost("/api/alerts", H((c, b) => Ok(alerts.Save(b))));
        app.MapDelete("/api/alerts/{id}", H((c, b) => { alerts.Delete(Route(c, "id")); return Ok(); }));
        app.MapPost("/api/alerts/stop", H(async (c, b) => { await alerts.StopAll(); return Ok(); }));
        app.MapPost("/api/alerts/volume", H((c, b) => { alerts.SetVolume(b.Str("id"), b.Int("volume")); return Ok(); }));
        app.MapPost("/api/alerts/toggle", H((c, b) => { alerts.SetEnabled(b.Str("id"), b.Bool("enabled")); return Ok(); }));

        // инструменты
        app.MapGet("/api/tools", H((c, b) => Ok(new
        {
            shazam = new
            {
                available = true, why = "", state = _hub.Shazam.State, last = _hub.Shazam.LastInfo(), devices = ShazamTool.Devices(),
            },
            translate = new { available = true, why = "", status = _hub.Translator.Status, url = _hub.S.Str("tools.translate.url") },
            yandex = new { available = true, has_token = _hub.Yandex.Token != "", status = _hub.Yandex.Status },
            youtube = new { status = _hub.YtDlp.Status },
            donationalerts = new { status = _hub.Donations.Status, connected = _hub.Donations.Connected },
            hotkeys = _hub.HotkeyStatus(),
            tray = _hub.TrayStatus,
        })));
        app.MapPost("/api/tools/shazam", H(async (c, b) =>
        {
            try { return Ok(await _hub.Shazam.Recognize()); }
            catch (InvalidOperationException e) { throw new UserError(e.Message); }
        }));
        app.MapPost("/api/tools/translate/test", H(async (c, b) =>
        {
            try
            {
                var (text, src) = await _hub.Translator.Translate(b.Str("text"));
                return Ok(new { text, source = src });
            }
            catch (InvalidOperationException e) { throw new UserError(e.Message); }
        }));
        app.MapPost("/api/tools/translate/languages", H(async (c, b) =>
        {
            try { return Ok(await _hub.Translator.Languages()); }
            catch (Exception e) when (e is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
            { throw new UserError($"Сервер перевода недоступен: {e.Message}"); }
        }));
        // мастер первого запуска: отправитель сообщений и отметка «пройден»
        app.MapPost("/api/wizard", H((c, b) =>
        {
            if (b.Str("sender") is "streamer" or "bot") _hub.S.Set("twitch.sender", b.Str("sender"));
            if (b["done"] != null) _hub.S.Set("ui.wizard_done", b.Bool("done"));
            _hub.Settings.Save();
            return Ok();
        }));
        app.MapPost("/api/report", H((c, b) =>
        {
            var path = Report.Create(_hub);
            Desktop.Shell.ShowFile(path);
            return Ok(new { path, name = Path.GetFileName(path) });
        }));
        app.MapPost("/api/tools/youtube-check", H(async (c, b) => Ok(await YouTubeCheck.Run(_hub))));
        app.MapPost("/api/tools/layout", H((c, b) => Ok(Layout.Fix(b.Str("text")) ?? "")));
        app.MapPost("/api/tools/yandex", H(async (c, b) =>
        {
            try { return Ok($"Токен работает: {await _hub.Yandex.Check()}"); }
            catch (Exception e) { throw new UserError($"Яндекс Музыка: {e.Message}"); }
        }));
        app.MapPost("/api/secret/clear", H((c, b) =>
        {
            var name = b.Str("name");
            if (name != "yandex" && name != "donationalerts") throw new UserError("Неизвестный ключ");
            if (name == "donationalerts") { _hub.Donations.Logout(); return Ok(); }
            _hub.S.Set(name + ".token", "");
            _hub.Settings.Save();
            return Ok();
        }));

        // резервные копии
        app.MapGet("/api/backups", H((c, b) => Ok(Backup.List())));
        app.MapPost("/api/backups", H((c, b) => Ok(Backup.Create())));
        app.MapPost("/api/backups/restore", H((c, b) =>
        {
            if (_hub.Stream.Live) throw new UserError("Восстановление доступно вне активной трансляции");
            Backup.Restore(b.Str("name"));
            return Ok(null, new() { ["message"] = "Копия восстановлена. Перезапустите приложение." });
        }));
    }

    private static bool IsCallbackPort(HttpContext c) => c.Connection.LocalPort == Config.CallbackPort;

    private static IResult NoStore(HttpContext c, IResult r)
    {
        c.Response.Headers["Cache-Control"] = "no-store";
        return r;
    }

    // ---------- OAuth callback ----------
    private Task<IResult> Callback(HttpContext ctx) =>
        CallbackPage(ctx, q => _hub.Twitch.HandleCallback(q), "Ошибка входа: ");

    private Task<IResult> DaCallback(HttpContext ctx) =>
        CallbackPage(ctx, q => _hub.Donations.HandleCallback(q), "Ошибка входа в DonationAlerts: ");

    private async Task<IResult> CallbackPage(HttpContext ctx, Func<Dictionary<string, string>, Task<(bool, string)>> handler, string errPrefix)
    {
        var q = ctx.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString());
        bool good;
        string text;
        try
        {
            (good, text) = await _hub.Loop.Call(() => handler(q));
        }
        catch (Exception e)
        {
            good = false;
            text = errPrefix + e.Message;
            _hub.Log.Error(text);
        }
        if (good) ShowWindow();
        var color = good ? "#2ecc71" : "#e74c3c";
        var html = "<!doctype html><meta charset=utf-8><title>Beg1nnnerOS</title>" +
                   "<body style='font-family:system-ui;background:#111;color:#eee;display:grid;place-items:center;height:100vh;margin:0'>" +
                   $"<div style='text-align:center'><h2 style='color:{color}'>{(good ? "Готово" : "Не получилось")}</h2>" +
                   $"<p>{WebUtility.HtmlEncode(text)}</p><p style='opacity:.6'>Это окно можно закрыть и вернуться в Beg1nnnerOS.</p></div>";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    // ---------- настройки ----------
    private IResult GetSettings()
    {
        var s = (JsonObject)_hub.S.DeepClone();
        foreach (var (sec, key) in Secrets)
        {
            var o = s.Obj(sec);
            o["has_" + key] = o.Str(key) != "";
            o[key] = "";
        }
        var tw = s.Obj("twitch");
        tw["has_secret"] = tw.Bool("has_client_secret");
        tw.Remove("has_client_secret");
        var tr = s.Obj("tools.translate");
        tr["has_api_key"] = tr.Str("api_key") != "";
        tr["api_key"] = "";
        var cloud = s.Obj("pro.cloud");
        cloud["has_password"] = cloud.Str("password") != "";
        cloud["password"] = "";
        var events = new JsonObject();
        foreach (var e in Config.EventTypes)
        {
            var vars = new JsonArray();
            foreach (var v in e.Vars) vars.Add(v);
            events[e.Key] = new JsonObject { ["title"] = e.Title, ["vars"] = vars };
        }
        var actions = new JsonObject();
        foreach (var kv in Hotkeys.Actions) actions[kv.Key] = kv.Value.Title;
        return Ok(s, new()
        {
            ["devices"] = Player.Devices(), ["access"] = Commands.Access, ["hotkey_actions"] = actions, ["event_types"] = events,
        });
    }

    private IResult SaveSettings(JsonNode body)
    {
        var data = _hub.S;
        var oldPort = data.Int("server.port");
        var oldDa = _hub.Donations.Token;
        string RewardsSig() => data["music"]["rewards"].ToJsonString() + data.Str("music.rewards_enabled") + data.Str("alerts.enabled");
        var oldRewards = RewardsSig();
        foreach (var sec in Sections)
        {
            if (body[sec] is not JsonObject incoming) continue;
            var nw = (JsonObject)J.DeepMerge(data[sec], incoming);
            foreach (var (ssec, key) in Secrets)
            {
                if (ssec != sec) continue;
                nw.Remove("has_" + key);
                nw.Remove("has_secret");
                if (incoming.Str(key).Trim() == "") nw[key] = data.Obj(sec).Str(key);   // пустое поле сохраняет прежнее
                nw[key] = nw.Str(key).Trim();
            }
            switch (sec)
            {
                case "twitch":
                    nw["client_id"] = nw.Str("client_id").Trim();
                    TwitchClient.CheckKeys(nw.Str("client_id"), nw.Str("client_secret"));
                    nw["bot_login"] = nw.Str("bot_login").Trim().TrimStart('@').ToLowerInvariant();
                    if (nw.Str("sender") != "streamer" && nw.Str("sender") != "bot") throw new UserError("Неизвестный отправитель");
                    break;
                case "music":
                    ValidateMusic(nw);
                    nw["order_sound_volume"] = Math.Max(0, Math.Min(100, nw.Int("order_sound_volume", 50)));
                    if (nw.Str("output") is not ("app" or "obs")) nw["output"] = "app";
                    nw["normalize_target"] = Math.Max(-24, Math.Min(-8, nw.Num("normalize_target", -14)));
                    if (!Chime.Styles.ContainsKey(nw.Str("order_sound_style"))) nw["order_sound_style"] = "bell";
                    if (nw.Str("search_first") != "youtube" && nw.Str("search_first") != "yandex") throw new UserError("Неизвестный источник поиска");
                    break;
                case "chat_overlay":
                    nw["font_size"] = Math.Max(10, Math.Min(72, nw.Int("font_size", 20) == 0 ? 20 : nw.Int("font_size", 20)));
                    nw["max_messages"] = Math.Max(1, Math.Min(60, nw.Int("max_messages", 20) == 0 ? 20 : nw.Int("max_messages", 20)));
                    nw["fade_sec"] = Math.Max(0, Math.Min(600, nw.Int("fade_sec", 0)));
                    if (nw["hidden_users"] is JsonValue)
                    {
                        var users = new JsonArray();
                        foreach (var u in nw.Str("hidden_users").Split('\n').Select(x => x.Trim()).Where(x => x != "")) users.Add(u);
                        nw["hidden_users"] = users;
                    }
                    break;
                case "events":
                    nw["interval_sec"] = Math.Max(1, Math.Min(60, nw.Int("interval_sec", 3)));
                    nw["gift_merge_sec"] = Math.Max(0, Math.Min(30, nw.Int("gift_merge_sec", 5)));
                    break;
                case "server":
                    var port = WholeNumber(nw["port"]);
                    if (port == null || port < 1024 || port > 65535)
                        throw RangeError("server.port", "Порт панели", 1024, 65535, nw["port"]);
                    if (port == Config.CallbackPort)
                        throw new UserError("«Порт панели»: 4343 занят входом в Twitch — выберите другой, например 8080", "server.port");
                    nw["port"] = port.Value;
                    break;
                case "hotkeys":
                    foreach (var k in Hotkeys.Actions.Keys)
                        if (nw.Str(k) != "") nw[k] = Hotkeys.Normalize(nw.Str(k));
                    break;
                case "tools":
                    var tr = nw.Obj("translate");
                    tr.Remove("has_api_key");
                    if (incoming.Str("translate.api_key").Trim() == "") tr["api_key"] = data.Str("tools.translate.api_key");
                    tr["source"] = tr.Str("source", "auto").Trim().ToLowerInvariant() is var src && src != "" ? src : "auto";
                    tr["target"] = tr.Str("target", "ru").Trim().ToLowerInvariant() is var dst && dst != "" ? dst : "ru";
                    nw.Obj("shazam")["interval"] = Math.Max(0, Math.Min(60, nw.Int("shazam.interval", 5)));
                    break;
                case "ui":
                    if (nw.Str("close_action") is not ("ask" or "tray" or "exit")) nw["close_action"] = "ask";
                    if (nw.Str("language") is not ("ru" or "en")) nw["language"] = "ru";
                    break;
                case "pro":
                    ValidatePro(nw, incoming, data.Obj("pro"));
                    break;
            }
            data[sec] = nw;
        }
        if (body["music"] is JsonObject)
        {
            var seen = new Dictionary<string, string>();
            foreach (var kv in data.Obj("music.rewards"))
            {
                var low = kv.Value.Str("title").Trim().ToLowerInvariant();
                if (seen.TryGetValue(low, out var other))
                    throw new UserError($"Награды «{RewardNames[other]}» и «{RewardNames.GetValueOrDefault(kv.Key, kv.Key)}» называются одинаково — " +
                                        "в Twitch у каждой награды должно быть своё название", $"music.rewards.{kv.Key}.title");
                seen[low] = kv.Key;
                var alert = _hub.Alerts.Items.FirstOrDefault(a => a.Str("reward_title").ToLowerInvariant() == low);
                if (alert != null)
                    throw new UserError($"Название «{kv.Value.Str("title")}» уже занято наградой МедиаАлерта «{alert.Str("name")}» — придумайте другое",
                                        $"music.rewards.{kv.Key}.title");
            }
        }
        _hub.Settings.Save();
        _hub.ApplyLanguage();
        _hub.Music.ApplySettings();
        if (_hub.Donations.Token != oldDa) _hub.Donations.Restart();
        if (body["twitch"] is JsonObject && _hub.Twitch.Streamer.Ready) _hub.EventSub.Restart();
        if (body["hotkeys"] is JsonObject) _hub.ApplyHotkeys();
        if (RewardsSig() != oldRewards) _hub.Rewards.ScheduleSync();
        return Ok(null, new() { ["restart_required"] = data.Int("server.port") != oldPort });
    }

    private static void ValidateMusic(JsonObject m)
    {
        foreach (var kv in m.Obj("rewards"))
        {
            var r = (JsonObject)kv.Value;
            var name = RewardNames.GetValueOrDefault(kv.Key, kv.Key);
            r["title"] = r.Str("title").Trim();
            var title = r.Str("title");
            if (title == "")
                throw new UserError($"Награда «{name}»: введите название, которое увидят зрители", $"music.rewards.{kv.Key}.title");
            if (title.Length > 45)
                throw new UserError($"Награда «{name}»: название длиннее 45 символов (сейчас {title.Length}) — Twitch такое не примет",
                                    $"music.rewards.{kv.Key}.title");
            var cost = WholeNumber(r["cost"]);
            if (cost == null || cost < 1)
                throw new UserError($"Награда «{name}»: цена должна быть не меньше 1 балла", $"music.rewards.{kv.Key}.cost");
            r["cost"] = cost.Value;
        }
        foreach (var (k, label, lo, hi) in MusicLimits)
        {
            var v = WholeNumber(m[k]);
            if (v == null || v < lo || v > hi) throw RangeError($"music.{k}", label, lo, hi, m[k]);
            m[k] = v.Value;
        }
        foreach (var k in new[] { "banned_words", "blocked_users", "blocked_tracks" })
            if (m[k] is JsonValue)
            {
                var arr = new JsonArray();
                foreach (var line in m.Str(k).Split('\n').Select(x => x.Trim()).Where(x => x != "")) arr.Add(line);
                m[k] = arr;
            }
    }

    // ---------- музыка ----------
    private async Task<IResult> MusicAction(string action, JsonNode b)
    {
        var m = _hub.Music;
        switch (action)
        {
            case "pause":
                return Ok(null, new() { ["paused"] = m.TogglePause() });
            case "skip":
                m.Skip("вручную");
                break;
            case "volume":
                m.SetVolume(b.Int("volume"));
                break;
            case "remove":
                var t = m.Remove(b.Str("id"));
                if (t?.Redemption != null && b.Bool("refund"))
                    await _hub.Rewards.Complete(t.Redemption.Value.RewardId, t.Redemption.Value.RedemptionId, false);
                break;
            case "move":
                m.Move(b.Str("id"), b.Int("delta"));
                break;
            case "clear":
                m.Queue.Clear();
                break;
            case "chime":   // проверка звука заказа из настроек (без сохранения)
            {
                var style = b.Str("style") != "" ? b.Str("style") : _hub.S.Str("music.order_sound_style", "bell");
                var vol = b["volume"] != null ? b.Int("volume") : _hub.S.Int("music.order_sound_volume", 50);
                try { m.Chime.Play(style, vol, _hub.S.Str("music.audio_device"), true); }
                catch (Exception e) { throw new UserError("Не удалось воспроизвести звук: " + e.Message); }
                break;
            }
            case "toast":
                m.ShowToast(b.Str("text") != "" ? b.Str("text") : "Тестовое уведомление Beg1nnnerOS");
                break;
            case "block":
                var cur = m.Current;
                if (cur != null)
                {
                    var blocked = _hub.S.Arr("music.blocked_tracks");
                    blocked.Add(cur.Vid != "" ? cur.Vid : cur.Title);
                    _hub.Settings.Save();
                    m.Skip("заблокирован");
                }
                break;
            default:
                return Fail("Неизвестное действие", 404);
        }
        return Ok();
    }

    private async Task<IResult> Upload(HttpContext ctx)
    {
        try
        {
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null) return Fail("Файл не передан");
            if (file.Length > 51L * 1024 * 1024) return Fail("Файл слишком большой");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var name = string.IsNullOrEmpty(file.FileName) ? "file" : file.FileName;
            var result = await _hub.Loop.CallSync(() => _hub.Alerts.SaveUpload(name, bytes));
            return Ok(result);
        }
        catch (UserError e) { return Fail(e.Message); }
        catch (Exception e) { return Fail($"Не удалось загрузить файл: {e.Message}", 500); }
    }

    // ---------- чат в OBS ----------
    private async Task WsChat(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var client = new AlertClient(ws) { Obs = ctx.Request.Query["obs"] == "1" };
        // история и подписка — в одном шаге цикла, чтобы не потерять сообщение между ними
        var history = await _hub.Loop.CallSync(() =>
        {
            _hub.ChatOverlay.Clients.Add(client);
            return _hub.ChatOverlay.HistoryJson();
        });
        await client.Send(history);
        var buf = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ctx.RequestAborted);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally
        {
            await _hub.Loop.CallSync(() => _hub.ChatOverlay.Clients.Remove(client));
        }
    }

    // ---------- источник /alerts ----------
    private async Task WsAlerts(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var client = new AlertClient(ws) { Obs = ctx.Request.Query["obs"] == "1" };
        await _hub.Loop.CallSync(() =>
        {
            _hub.Alerts.Clients.Add(client);
            _hub.Log.Add("alert", "Источник /alerts подключён");
            return true;
        });
        await client.Send(J.Serialize(new { type = "hello", master = _hub.S.Int("alerts.volume", 80) }));
        var buf = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ctx.RequestAborted);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
        finally
        {
            await _hub.Loop.CallSync(() => _hub.Alerts.Clients.Remove(client));
        }
    }
}
