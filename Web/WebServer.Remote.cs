using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Beg1nnnerOS.Web;

/// <summary>
/// «Пульт с телефона»: отдельный маленький сервер в домашней сети (Wi-Fi), только когда пульт включён.
/// Пускает лишь по секретному ключу из QR-кода и умеет только безопасные действия:
/// музыка, громкость, пауза уведомлений, остановка алертов, сообщение в чат. Настройки и входы с телефона недоступны.
/// </summary>
public sealed partial class WebServer
{
    private WebApplication _remote;
    private JsonStore _remoteStore;
    private readonly ConcurrentDictionary<string, (int Fails, double Since)> _remoteFails = new();

    private JsonStore RemoteStore => _remoteStore ??= new JsonStore(Path.Combine(Config.Profile, "remote.json"),
        new JsonObject { ["enabled"] = false, ["port"] = 8081, ["token"] = "" });
    private JsonObject RemoteCfg => RemoteStore.Data;
    public bool RemoteRunning => _remote != null;
    public string RemoteError { get; private set; } = "";

    private static string NewToken()
    {
        const string abc = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(22);
        return new string(bytes.Select(b => abc[b % abc.Length]).ToArray());
    }

    /// <summary>Адреса этого компьютера в домашней сети (сначала 192.168.*, потом 10.*, 172.16–31.*).</summary>
    public static List<string> LanAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vpn") || desc.Contains("wsl")) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.Address.GetAddressBytes();
                    var priv = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                    if (priv) list.Add(a.Address.ToString());
                }
            }
        }
        catch { }
        return list.Distinct().OrderBy(ip => ip.StartsWith("192.168.") ? 0 : ip.StartsWith("10.") ? 1 : 2).ToList();
    }

    public object RemoteInfo()
    {
        var port = RemoteCfg.Int("port", 8081);
        var token = RemoteCfg.Str("token");
        var ips = LanAddresses();
        return new
        {
            enabled = RemoteCfg.Bool("enabled"), running = RemoteRunning, error = RemoteError, port,
            addresses = ips.Select(ip => $"http://{ip}:{port}/?k={token}").ToList(),
        };
    }

    public async Task<object> RemoteSet(bool enabled, bool newKey)
    {
        if (newKey || RemoteCfg.Str("token") == "") RemoteCfg["token"] = NewToken();
        RemoteCfg["enabled"] = enabled;
        RemoteStore.Save();
        await StopRemote();
        if (enabled) await StartRemote();
        return RemoteInfo();
    }

    public async Task StartRemote()
    {
        RemoteError = "";
        if (!RemoteCfg.Bool("enabled")) return;
        if (RemoteCfg.Str("token") == "") { RemoteCfg["token"] = NewToken(); RemoteStore.Save(); }
        var port = RemoteCfg.Int("port", 8081);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16 * 1024);
            var app = builder.Build();
            MapRemote(app);
            await app.StartAsync();
            _remote = app;
            _hub.Log.Add("system", $"Пульт с телефона включён (порт {port})");
        }
        catch (Exception e)
        {
            RemoteError = e is IOException || e.InnerException is SocketException
                ? $"порт {port} занят другой программой"
                : e.Message;
            _hub.Log.Error("Пульт с телефона: " + RemoteError);
        }
    }

    public async Task StopRemote()
    {
        var app = _remote;
        _remote = null;
        if (app == null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(cts.Token);
            await app.DisposeAsync();
        }
        catch { }
    }

    /// <summary>Ключ из ?k= или заголовка X-Key; после 20 неверных попыток за 10 минут адрес блокируется.</summary>
    private bool RemoteAllowed(HttpContext c)
    {
        var ip = c.Connection.RemoteIpAddress?.ToString() ?? "?";
        var now = ActionLog.Now;
        if (_remoteFails.TryGetValue(ip, out var f) && now - f.Since < 600 && f.Fails >= 20) return false;
        var given = c.Request.Query["k"].ToString();
        if (given == "") given = c.Request.Headers["X-Key"].ToString();
        var token = RemoteCfg.Str("token");
        var ok = token != "" && given.Length == token.Length &&
                 CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
        if (!ok)
        {
            _remoteFails.AddOrUpdate(ip, (1, now), (_, old) => now - old.Since > 600 ? (1, now) : (old.Fails + 1, old.Since));
            return false;
        }
        return true;
    }

    private void MapRemote(WebApplication app)
    {
        app.MapGet("/", (HttpContext c) =>
        {
            c.Response.Headers["Cache-Control"] = "no-store";
            c.Response.Headers["X-Frame-Options"] = "DENY";
            if (!RemoteAllowed(c))
                return Results.Text("<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'>" +
                    "<body style='font:16px system-ui;background:#09090c;color:#f4f4f7;padding:32px'><h2>Beg1nnnerOS</h2>" +
                    "<p>Ссылка устарела или неполная. Отсканируйте QR-код заново: «Эфир» → «Пульт с телефона».</p>", "text/html; charset=utf-8", Encoding.UTF8, 403);
            return Results.File(Path.Combine(Config.WebDir, "remote.html"), "text/html; charset=utf-8");
        });
        app.MapGet("/fonts/{name}", (HttpContext c) =>
        {
            var name = Route(c, "name");
            if (name is not ("Geist.woff2" or "GeistMono.woff2")) return Results.NotFound();
            return Results.File(Path.Combine(Config.WebDir, "fonts", name), "font/woff2");
        });
        // язык интерфейса: i18n.js и словарь (без ключа — в них нет ничего личного)
        app.MapGet("/api/lang", (HttpContext c) => Json(new { ok = true, data = new { lang = L.Lang } }));
        app.MapGet("/static/i18n.js", () => Results.File(Path.Combine(Config.WebDir, "i18n.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/static/i18n/en.json", () => Results.File(Path.Combine(Config.WebDir, "i18n", "en.json"), "application/json; charset=utf-8"));
        app.MapGet("/api/state", R((c, b) => Ok(RemoteState())));
        app.MapPost("/api/act", R(async (c, b) => Ok(await RemoteAct(b.Str("action"), b))));
    }

    /// <summary>Как H, но сначала проверяет ключ пульта.</summary>
    private Delegate R(Func<HttpContext, JsonNode, Task<IResult>> handler) => async (HttpContext ctx) =>
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        if (!RemoteAllowed(ctx)) return Fail("Нет доступа — отсканируйте QR-код заново", 403);
        var body = ctx.Request.Method == "POST" ? await Body(ctx) : new JsonObject();
        try { return await _hub.Loop.Call(() => handler(ctx, body)); }
        catch (UserError e) { return Fail(e.Message); }
        catch (Exception e) { return Fail("Не получилось: " + e.Message, 500); }
    };
    private Delegate R(Func<HttpContext, JsonNode, IResult> handler) => R((ctx, b) => Task.FromResult(handler(ctx, b)));

    private object RemoteState()
    {
        return new
        {
            music = _hub.Music.State(),
            paused_events = _hub.Events.Paused,
            alerts_pending = _hub.Alerts.Pending.Count,
            live = _hub.Stream.Live, uptime = _hub.Stream.UptimeText(),
            can_chat = _hub.Twitch.SenderReady(),
            channel = _hub.Twitch.Streamer.Login,
            theme = _hub.S.Str("ui.theme", "studio"),
        };
    }

    private async Task<object> RemoteAct(string action, JsonNode b)
    {
        var m = _hub.Music;
        switch (action)
        {
            case "pause": m.TogglePause(); break;
            case "skip": m.Skip("с телефона"); break;
            case "volume": m.SetVolume(Math.Clamp(b.Int("value"), 0, 100)); break;
            case "like":
                var (liked, msg) = m.ToggleLikeCurrent();
                return new { message = msg, liked };
            case "block":
                if (m.Current == null) throw new UserError("Сейчас ничего не играет");
                return new { message = m.BlockCurrent("track") };
            case "pause_events": _hub.Events.SetPaused(!_hub.Events.Paused); break;
            case "stop_alerts": await _hub.Alerts.StopAll(); break;
            case "add":
            {
                var q = b.Str("query").Trim();
                if (q == "") throw new UserError("Вставьте ссылку или название трека");
                var res = await m.Request(q.Length > 300 ? q[..300] : q, "пульт", "", 0, true);
                if (!res.Ok) throw new UserError(res.Text);
                return new { message = res.Text };
            }
            case "chat":
            {
                var text = b.Str("text").Trim();
                if (text == "") throw new UserError("Пустое сообщение");
                if (!await _hub.Twitch.SendChat(text.Length > 450 ? text[..450] : text))
                    throw new UserError(_hub.Twitch.LastSendError != "" ? _hub.Twitch.LastSendError : "Сообщение не отправлено");
                return new { message = "Отправлено в чат" };
            }
            default: throw new UserError("Неизвестное действие");
        }
        return new { message = "" };
    }
}
