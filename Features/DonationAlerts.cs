using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>DonationAlerts: сообщения о донатах через официальный API и Centrifugo (раздел 12).</summary>
public sealed class DonationAlerts
{
    public static string ApiUrl = "https://www.donationalerts.com/api/v1";
    public static string WsUrl = "wss://centrifugo.donationalerts.com/connection/websocket";
    public static string AuthUrl = "https://www.donationalerts.com/oauth/authorize";
    public static string TokenUrl = "https://www.donationalerts.com/oauth/token";
    public const string RedirectUri = "http://localhost:4343/da";
    public const string Scopes = "oauth-user-show oauth-donation-subscribe";

    private readonly Hub _hub;
    public bool Connected;
    public string Status = "не подключено";
    public string User = "";
    private CancellationTokenSource _restart = new();
    private readonly LinkedList<string> _seen = new();
    private readonly Dictionary<string, double> _states = new();

    public DonationAlerts(Hub hub) { _hub = hub; }

    public string Token => _hub.S.Str("donationalerts.token").Trim();
    private string ClientId => _hub.S.Str("donationalerts.client_id").Trim();
    private string ClientSecret => _hub.S.Str("donationalerts.client_secret").Trim();

    // ---------- вход через DonationAlerts (OAuth, authorization code) ----------
    public string BuildAuthUrl()
    {
        if (ClientId == "" || ClientSecret == "")
            throw new UserError("Сначала вставьте ID приложения и API-ключ DonationAlerts и сохраните настройки");
        if (!ClientId.All(char.IsDigit))
            throw new UserError("ID приложения DonationAlerts — это число (например 12345). Проверьте, что поля не перепутаны", "donationalerts.client_id");
        var now = ActionLog.Now;
        foreach (var k in _states.Where(p => now - p.Value > 900).Select(p => p.Key).ToList()) _states.Remove(k);
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _states[state] = now;
        var q = new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["redirect_uri"] = RedirectUri, ["response_type"] = "code",
            ["scope"] = Scopes, ["state"] = state,
        };
        return AuthUrl + "?" + string.Join("&", q.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
    }

    public async Task<(bool Ok, string Text)> HandleCallback(IDictionary<string, string> p)
    {
        if (p.TryGetValue("error", out var err) && err != "")
            return (false, p.TryGetValue("error_description", out var d) && d != "" ? d : err);
        p.TryGetValue("state", out var st);
        p.TryGetValue("code", out var code);
        bool known;
        if (!string.IsNullOrEmpty(st)) known = _states.Remove(st);
        else
        {   // на случай, если DonationAlerts не вернёт state: принимаем единственный свежий запрос
            known = _states.Count == 1;
            _states.Clear();
        }
        if (!known || string.IsNullOrEmpty(code))
            return (false, "Неизвестный или устаревший запрос входа. Нажмите «Войти через DonationAlerts» в программе ещё раз.");
        await TokenRequest(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
        });
        var me = (await Api(HttpMethod.Get, "/user/oauth"))["data"];
        var name = me.Str("name") != "" ? me.Str("name") : me.Str("id");
        _hub.Log.Add("system", "Вход DonationAlerts: " + name);
        Restart();
        return (true, "DonationAlerts подключён: " + name);
    }

    private async Task TokenRequest(Dictionary<string, string> data)
    {
        data["client_id"] = ClientId;
        data["client_secret"] = ClientSecret;
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(data) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _hub.Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var js = Twitch.TwitchClient.SafeParse(text);
        if (!resp.IsSuccessStatusCode || js.Str("access_token") == "")
        {
            var e = js.Str("error");
            var msg = js.Str("error_description", js.Str("message", e != "" ? e : (text.Length > 150 ? text[..150] : text)));
            if (e == "invalid_client")
                msg = "неверный ID приложения или API-ключ — скопируйте их заново со страницы приложения DonationAlerts";
            else if (e == "invalid_grant" && data["grant_type"] == "refresh_token")
                msg = "вход устарел — нажмите «Войти через DonationAlerts» ещё раз";
            throw new DaException(msg);
        }
        _hub.S.Set("donationalerts.token", js.Str("access_token"));
        if (js.Str("refresh_token") != "") _hub.S.Set("donationalerts.refresh_token", js.Str("refresh_token"));
        var expiresIn = js.Num("expires_in");
        _hub.S.Set("donationalerts.expires_at", expiresIn > 0 ? (long)(ActionLog.Now + expiresIn) : 0L);
        _hub.Settings.Save();
    }

    private async Task<bool> Refresh()
    {
        var rt = _hub.S.Str("donationalerts.refresh_token");
        if (rt == "" || ClientSecret == "") return false;
        await TokenRequest(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = rt, ["scope"] = Scopes,
        });
        _hub.Log.Add("system", "Токен DonationAlerts обновлён");
        return true;
    }

    public void Logout()
    {
        _hub.S.Set("donationalerts.token", "");
        _hub.S.Set("donationalerts.refresh_token", "");
        _hub.S.Set("donationalerts.expires_at", 0L);
        _hub.Settings.Save();
        Restart();
    }

    public void Restart()
    {
        var old = _restart;
        _restart = new CancellationTokenSource();
        try { old.Cancel(); } catch { }
    }

    private sealed class DaException : Exception
    {
        public DaException(string m) : base(m) { }
    }

    private async Task<JsonNode> Api(HttpMethod method, string path, object body = null, bool retry = true)
    {
        using var req = new HttpRequestMessage(method, ApiUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body != null) req.Content = new StringContent(J.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _hub.Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 401)
        {
            if (retry && await Refresh()) return await Api(method, path, body, false);
            throw new DaException("вход устарел — нажмите «Войти через DonationAlerts» ещё раз");
        }
        if (!resp.IsSuccessStatusCode)
            throw new DaException($"{(int)resp.StatusCode}: {(text.Length > 120 ? text[..120] : text)}");
        return Twitch.TwitchClient.SafeParse(text);
    }

    private async Task Wait(double sec)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(sec), _restart.Token); }
        catch (OperationCanceledException) { }
    }

    public async Task Run()
    {
        var backoff = 3.0;
        while (true)
        {
            var token = _restart.Token;
            if (Token == "")
            {
                Connected = false;
                Status = "не подключено — нажмите «Войти через DonationAlerts»";
                await Wait(30);
                continue;
            }
            try
            {
                var exp = _hub.S.Num("donationalerts.expires_at");
                if (exp > 0 && exp - ActionLog.Now < 86400) await Refresh();   // заранее продлеваем токен
                await Session(token);
                backoff = 3;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { backoff = 1; }
            catch (DaException e)
            {
                Status = "ошибка: " + e.Message;
                _hub.Log.Error("DonationAlerts: " + e.Message);
                backoff = 60;
            }
            catch (Exception e)
            {
                Status = $"соединение потеряно ({e.GetType().Name})";
                _hub.Log.Error($"DonationAlerts: {e.GetType().Name}: {e.Message}");
            }
            Connected = false;
            await Wait(backoff);
            backoff = Math.Min(backoff * 2, 120);
        }
    }

    private static async Task<string> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new IOException("сокет закрыт");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Task Send(ClientWebSocket ws, object msg) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(J.Serialize(msg))), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task Session(CancellationToken restart)
    {
        var me = (await Api(HttpMethod.Get, "/user/oauth"))["data"];
        var uid = me.Str("id");
        var sockToken = me.Str("socket_connection_token");
        User = me.Str("name") != "" ? me.Str("name") : uid;
        var channel = $"$alerts:donation_{uid}";
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(WsUrl), restart);
        await Send(ws, new { @params = new { token = sockToken }, id = 1 });
        var seq = 2;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(restart);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            string text;
            try { text = await Receive(ws, timeout.Token); }
            catch (OperationCanceledException) when (!restart.IsCancellationRequested)
            {
                // Тишина 25 секунд: соединение после отмены приёма закрыто — переподключаемся
                throw new TimeoutException("нет данных от Centrifugo");
            }
            foreach (var line in text.Split('\n'))
            {
                if (line.Trim() == "") continue;
                var data = JsonNode.Parse(line);
                if (data?["error"] != null) throw new DaException("Centrifugo: " + data["error"].ToJsonString());
                var res = data?["result"];
                if (data.Int("id") == 1 && res.Str("client") != "")
                {
                    var sub = await Api(HttpMethod.Post, "/centrifuge/subscribe",
                        new { channels = new[] { channel }, client = res.Str("client") });
                    var chToken = (sub["channels"] as JsonArray ?? new JsonArray())
                        .FirstOrDefault(c => c.Str("channel") == channel).Str("token");
                    await Send(ws, new { @params = new { channel, token = chToken }, method = 1, id = seq++ });
                    Connected = true;
                    Status = $"подключено ({User})";
                    _hub.Log.Add("system", "DonationAlerts подключён");
                    _ = Pinger(ws, restart);
                }
                else if (res.Str("channel") == channel)
                {
                    var inner = res.At("data.data");
                    if (inner is JsonObject) OnDonation(inner);
                }
            }
        }
    }

    private static async Task Pinger(ClientWebSocket ws, CancellationToken ct)
    {
        var id = 1000;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await Task.Delay(15000, ct);
                await Send(ws, new { method = 7, id = id++ });
            }
        }
        catch { }
    }

    public void OnDonation(JsonNode d)
    {
        var did = d.Str("id");
        if (did != "")
        {
            if (_seen.Contains(did)) return;
            _seen.AddLast(did);
            while (_seen.Count > 200) _seen.RemoveFirst();
        }
        var name = d.Str("username").Trim();
        if (name == "") name = "Аноним";
        var amount = d.Num("amount");
        var amountText = Math.Abs(amount % 1) < 1e-9 ? ((long)amount).ToString() : amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _hub.Events.Notify("donation", new()
        {
            ["user"] = name, ["amount"] = amountText, ["currency"] = d.Str("currency"), ["message"] = d.Str("message").Trim(),
        });
    }
}
