using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Twitch;

/// <summary>Twitch EventSub по WebSocket: чат и события канала через одно соединение.</summary>
public sealed class EventSub
{
    public static string WsUrl = "wss://eventsub.wss.twitch.tv/ws";

    private readonly Hub _hub;
    public bool Connected;
    public string SessionId = "";
    public Dictionary<string, string> Failed = new();
    public List<string> Active = new();
    private readonly LinkedList<string> _seen = new();
    private CancellationTokenSource _restart = new();
    private ClientWebSocket _oldWs;
    private int _keepalive = 10;

    public EventSub(Hub hub) { _hub = hub; }

    public void Restart()
    {
        var old = _restart;
        _restart = new CancellationTokenSource();
        try { old.Cancel(); } catch { }
    }

    private static List<(string Type, string Version, Dictionary<string, string> Cond)> Subscriptions(string bid)
    {
        Dictionary<string, string> B() => new() { ["broadcaster_user_id"] = bid };
        Dictionary<string, string> With(string k, string v) { var d = B(); d[k] = v; return d; }
        return new()
        {
            ("channel.chat.message", "1", With("user_id", bid)),
            ("channel.chat.message_delete", "1", With("user_id", bid)),
            ("channel.chat.clear_user_messages", "1", With("user_id", bid)),
            ("channel.chat.clear", "1", With("user_id", bid)),
            ("stream.online", "1", B()),
            ("stream.offline", "1", B()),
            ("channel.update", "2", B()),
            ("channel.follow", "2", With("moderator_user_id", bid)),
            ("channel.subscribe", "1", B()),
            ("channel.subscription.gift", "1", B()),
            ("channel.subscription.message", "1", B()),
            ("channel.cheer", "1", B()),
            ("channel.raid", "1", new Dictionary<string, string> { ["to_broadcaster_user_id"] = bid }),
            ("channel.channel_points_custom_reward_redemption.add", "1", B()),
            ("channel.hype_train.begin", "2", B()),
            ("channel.hype_train.end", "2", B()),
            ("channel.poll.begin", "1", B()),
            ("channel.poll.end", "1", B()),
            ("channel.ad_break.begin", "1", B()),
        };
    }

    private async Task WaitRestart(double seconds)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), _restart.Token); }
        catch (OperationCanceledException) { }
    }

    public async Task Run()
    {
        var tw = _hub.Twitch;
        var backoff = 2.0;
        while (true)
        {
            if (!tw.Streamer.Ready || tw.Streamer.ClientId == "")
            {
                Connected = false;
                await WaitRestart(5);
                continue;
            }
            var token = _restart.Token;
            if (token.IsCancellationRequested) { _restart = new CancellationTokenSource(); token = _restart.Token; }
            try
            {
                string url = WsUrl;
                var resub = true;
                while (url != null)
                {
                    url = await Session(url, resub, token);
                    resub = false;
                }
                backoff = 2;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                backoff = 1;
            }
            catch (Exception e)
            {
                _hub.Log.Error($"EventSub: соединение потеряно ({e.GetType().Name}: {e.Message}). Переподключение…");
            }
            Connected = false;
            await WaitRestart(backoff);
            backoff = Math.Min(backoff * 2, 60);
        }
    }

    private static async Task<string> ReceiveText(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new IOException($"сокет закрыт ({ws.CloseStatus})");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Одно соединение. Возвращает reconnect_url или бросает исключение.</summary>
    private async Task<string> Session(string url, bool resub, CancellationToken restart)
    {
        var ws = new ClientWebSocket();
        var handedOver = false;
        try
        {
            await ws.ConnectAsync(new Uri(url), restart);
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(restart);
                timeout.CancelAfter(TimeSpan.FromSeconds(_keepalive + 15));
                string text;
                try
                {
                    text = await ReceiveText(ws, timeout.Token);
                }
                catch (OperationCanceledException) when (!restart.IsCancellationRequested)
                {
                    throw new TimeoutException("нет keepalive от Twitch");
                }
                var data = JsonNode.Parse(text);
                var mtype = data.Str("metadata.message_type");
                var payload = data["payload"];
                switch (mtype)
                {
                    case "session_welcome":
                        SessionId = payload.Str("session.id");
                        _keepalive = Math.Max(5, payload.Int("session.keepalive_timeout_seconds", 10));
                        Connected = true;
                        if (_oldWs != null)
                        {
                            try { await _oldWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                            _oldWs.Dispose();
                            _oldWs = null;
                        }
                        if (resub) await SubscribeAll();
                        _hub.Log.Add("system", "EventSub подключён");
                        break;
                    case "notification":
                        var mid = data.Str("metadata.message_id");
                        if (_seen.Contains(mid)) break;
                        _seen.AddLast(mid);
                        while (_seen.Count > 500) _seen.RemoveFirst();
                        var stype = payload.Str("subscription.type");
                        var ev = payload["event"]?.DeepClone() ?? new JsonObject();
                        _hub.Loop.Start(() => Dispatch(stype, ev));
                        break;
                    case "session_reconnect":
                        _oldWs = ws;
                        handedOver = true;
                        return payload.Str("session.reconnect_url");
                    case "revocation":
                        var t = payload.Str("subscription.type", "?");
                        Failed[t] = "отозвано: " + payload.Str("subscription.status");
                        _hub.Log.Error($"EventSub: подписка {t} отозвана ({payload.Str("subscription.status")}). " +
                                       "Возможно, нужно войти как стример заново.");
                        break;
                }
            }
        }
        finally
        {
            if (!handedOver)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch { }
                ws.Dispose();
            }
        }
    }

    private async Task SubscribeAll()
    {
        var tw = _hub.Twitch;
        Failed = new Dictionary<string, string>();
        Active = new List<string>();
        foreach (var (type, version, cond) in Subscriptions(tw.BroadcasterId))
        {
            object Body(string v) => new
            {
                type, version = v, condition = cond,
                transport = new { method = "websocket", session_id = SessionId },
            };
            try
            {
                await tw.Api(HttpMethod.Post, "/eventsub/subscriptions", body: Body(version));
                Active.Add(type);
            }
            catch (TwitchException e)
            {
                var err = e;
                if (type.StartsWith("channel.hype_train") && version == "2")
                {
                    try
                    {
                        await tw.Api(HttpMethod.Post, "/eventsub/subscriptions", body: Body("1"));
                        Active.Add(type);
                        continue;
                    }
                    catch (TwitchException e2) { err = e2; }
                }
                if (err.Status == 409) { Active.Add(type); continue; }
                Failed[type] = err.Reason;
                // Отказ одного типа не должен отключать остальные функции
                _hub.Log.Add("skip", $"EventSub: «{type}» недоступен ({err.Status} {err.Reason})");
            }
        }
    }

    private async Task Dispatch(string stype, JsonNode ev)
    {
        try { await _hub.OnEventSub(stype, ev); }
        catch (Exception e) { _hub.Log.Error($"Ошибка обработки {stype}: {e.GetType().Name}: {e.Message}"); }
    }
}
