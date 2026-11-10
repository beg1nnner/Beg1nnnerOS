using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Twitch;

public sealed class TwitchException : Exception
{
    public int Status { get; }
    public TwitchException(int status, string message) : base($"{status}: {message}") { Status = status; Reason = message; }
    public string Reason { get; }
}

/// <summary>Один вход Twitch (стример или отдельный бот).</summary>
public sealed class TwitchAccount
{
    private readonly TwitchClient _tw;
    public readonly JsonStore Store;
    public readonly string Kind;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public string Problem = "";

    public TwitchAccount(TwitchClient tw, JsonStore store, string kind)
    {
        _tw = tw; Store = store; Kind = kind;
    }

    private JsonObject D => Store.Data;
    public bool Ready => D.Str("access_token") != "" && D.Str("user_id") != "";
    public string UserId => D.Str("user_id");
    public string Login => D.Str("login");
    public List<string> Scopes => D.StrList("scopes");
    /// <summary>Вход через встроенное приложение (по коду, без своих ключей).</summary>
    public bool IsPublic => D.Bool("public");
    /// <summary>Client ID, которым выдан токен: запросы к Twitch обязаны идти с ним же.</summary>
    public string ClientId => D.Str("client_id") != "" ? D.Str("client_id") : _tw.ClientId;

    public List<string> MissingScopes()
    {
        var need = Kind == "streamer" ? TwitchClient.StreamerScopes : TwitchClient.BotScopes;
        var have = Scopes;
        return need.Where(s => !have.Contains(s)).ToList();
    }

    public void Logout()
    {
        Store.Data = new JsonObject();
        Store.Save();
    }

    public void SetTokens(JsonNode tok, JsonNode info, bool isPublic = false)
    {
        var scopes = new JsonArray();
        if (info?["scopes"] is JsonArray sc) foreach (var s in sc) scopes.Add(J.AsStr(s));
        Store.Data = new JsonObject
        {
            ["access_token"] = tok.Str("access_token"),
            ["refresh_token"] = tok.Str("refresh_token"),
            ["expires_at"] = ActionLog.Now + tok.Num("expires_in", 3600),
            ["user_id"] = info.Str("user_id"),
            ["login"] = info.Str("login"),
            ["scopes"] = scopes,
            ["client_id"] = info.Str("client_id"),
            ["public"] = isPublic,
        };
        Store.Save();
        Problem = "";
    }

    public async Task<string> Token(bool forceRefresh = false)
    {
        await _lock.WaitAsync();
        try
        {
            if (D.Str("access_token") == "") throw new TwitchException(401, "нет входа в Twitch");
            if (forceRefresh || ActionLog.Now > D.Num("expires_at") - 120)
            {
                var rt = D.Str("refresh_token");
                if (rt == "") throw new TwitchException(401, "требуется повторный вход");
                JsonNode tok;
                try
                {
                    tok = await _tw.OAuthToken(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = rt }, this);
                }
                catch (TwitchException e)
                {
                    Problem = "Требуется повторный вход в Twitch";
                    throw new TwitchException(401, $"не удалось обновить токен ({e.Reason}); войдите заново");
                }
                D["access_token"] = tok.Str("access_token");
                D["refresh_token"] = tok.Str("refresh_token", rt) == "" ? rt : tok.Str("refresh_token", rt);
                D["expires_at"] = ActionLog.Now + tok.Num("expires_in", 3600);
                if (tok["scope"] is JsonArray sc) D["scopes"] = sc.DeepClone();
                Store.Save();
            }
            return D.Str("access_token");
        }
        finally { _lock.Release(); }
    }
}

public sealed class TwitchClient
{
    public static string AuthUrl = "https://id.twitch.tv/oauth2/authorize";
    public static string TokenUrl = "https://id.twitch.tv/oauth2/token";
    public static string ValidateUrl = "https://id.twitch.tv/oauth2/validate";
    public static string Helix = "https://api.twitch.tv/helix";

    public static readonly string[] StreamerScopes =
    {
        "user:read:chat", "user:write:chat", "user:bot", "channel:bot",
        "moderator:read:followers", "channel:read:subscriptions", "bits:read",
        "channel:read:redemptions", "channel:manage:redemptions",
        "channel:read:hype_train", "channel:read:polls", "channel:read:ads",
        "channel:read:vips", "moderator:read:chatters",
    };
    public static readonly string[] BotScopes = { "user:read:chat", "user:write:chat", "user:bot" };

    /// <summary>
    /// Client ID встроенного приложения Beg1nnnerOS (тип клиента «Public» на dev.twitch.tv).
    /// С ним вход идёт по коду (Device Code Flow): пользователю не нужны свои Client ID и Secret.
    /// Пусто — быстрый вход скрыт, остаётся вход со своими ключами.
    /// </summary>
    public const string BuiltInClientId = "";
    public const string DeviceUrl = "https://id.twitch.tv/oauth2/device";

    private readonly Hub _hub;
    public readonly HttpClient Http;
    public readonly TwitchAccount Streamer;
    public readonly TwitchAccount Bot;
    private readonly Dictionary<string, (string Kind, double Ts)> _states = new();
    public readonly LinkedList<string> SentIds = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private double _lastSend;
    public string LastSendError = "";

    public TwitchClient(Hub hub)
    {
        _hub = hub;
        Http = hub.Http;
        Streamer = new TwitchAccount(this, new JsonStore(System.IO.Path.Combine(Config.Profile, "tokens.json")), "streamer");
        Bot = new TwitchAccount(this, new JsonStore(System.IO.Path.Combine(Config.Profile, "bot_tokens.json")), "bot");
    }

    private JsonObject Cfg => _hub.S.Obj("twitch");
    public string ClientId => Cfg.Str("client_id").Trim();
    /// <summary>ID для входа по коду: встроенный или из настройки twitch.public_client_id (для проверки).</summary>
    public string PublicClientId => BuiltInClientId != "" ? BuiltInClientId : Cfg.Str("public_client_id").Trim();
    public bool QuickLoginAvailable => PublicClientId != "";
    public bool OwnKeys => ClientId != "" && ClientSecret != "";
    private string ClientSecret => Cfg.Str("client_secret").Trim();
    public string BroadcasterId => Streamer.UserId;
    public TwitchAccount Sender => Cfg.Str("sender") == "bot" ? Bot : Streamer;
    public bool SenderReady() => Cfg.Bool("allow_send") && Streamer.Ready && Sender.Ready;

    // ---------- OAuth ----------
    private static readonly System.Text.RegularExpressions.Regex KeyRx = new("^[a-z0-9]{20,40}$");

    /// <summary>
    /// Client ID и Client Secret у Twitch — строки из ~30 строчных латинских букв и цифр.
    /// Частая ошибка — вписать название приложения: Twitch тогда отвечает «invalid client».
    /// </summary>
    public static void CheckKeys(string clientId, string secret)
    {
        if (clientId != "" && !KeyRx.IsMatch(clientId))
            throw new UserError(clientId.Contains(' ') || clientId.Any(char.IsUpper) || clientId.Length < 20
                ? $"«{clientId}» — похоже на название приложения, а не Client ID. Откройте dev.twitch.tv/console/apps → «Управление» и скопируйте «Идентификатор клиента» (около 30 латинских букв и цифр)."
                : "Client ID должен состоять из ~30 строчных латинских букв и цифр — скопируйте его заново на dev.twitch.tv/console/apps", "client_id");
        if (secret != "" && !KeyRx.IsMatch(secret))
            throw new UserError("Client Secret должен состоять из ~30 строчных латинских букв и цифр. На dev.twitch.tv/console/apps → «Управление» нажмите «Создать» у секретного ключа и скопируйте его.", "client_secret");
    }
    public string BuildAuthUrl(string kind)
    {
        if (ClientId == "" || ClientSecret == "")
            throw new UserError("Сначала укажите и сохраните Client ID и Client Secret");
        CheckKeys(ClientId, ClientSecret);
        var now = ActionLog.Now;
        foreach (var k in _states.Where(p => now - p.Value.Ts > 900).Select(p => p.Key).ToList()) _states.Remove(k);
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _states[state] = (kind, now);
        var scopes = kind == "streamer" ? StreamerScopes : BotScopes;
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = ClientId, ["redirect_uri"] = Config.RedirectUri,
            ["scope"] = string.Join(" ", scopes), ["state"] = state, ["force_verify"] = "true",
        };
        return AuthUrl + "?" + string.Join("&", q.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
    }

    public async Task<(bool Ok, string Text)> HandleCallback(IDictionary<string, string> p)
    {
        if (p.TryGetValue("error", out var err) && err != "")
            return (false, p.TryGetValue("error_description", out var d) && d != "" ? d : err);
        p.TryGetValue("state", out var st);
        p.TryGetValue("code", out var code);
        if (st == null || !_states.TryGetValue(st, out var entry) || string.IsNullOrEmpty(code))
            return (false, "Неизвестный или устаревший запрос входа. Начните вход из приложения заново.");
        _states.Remove(st);
        var tok = await OAuthToken(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Config.RedirectUri,
        });
        var info = await Validate(tok.Str("access_token"));
        if (entry.Kind == "bot")
        {
            var expected = Cfg.Str("bot_login").Trim().TrimStart('@').ToLowerInvariant();
            if (expected != "" && info.Str("login").ToLowerInvariant() != expected)
                return (false, $"Вход выполнен как {info.Str("login")}, а в настройках указан бот {expected}. " +
                               "Переключитесь в браузере на аккаунт бота и повторите.");
        }
        var acc = entry.Kind == "streamer" ? Streamer : Bot;
        acc.SetTokens(tok, info);
        _hub.Log.Add("system", $"Вход Twitch ({(entry.Kind == "streamer" ? "стример" : "бот")}): {info.Str("login")}");
        _hub.OnAccountChanged(entry.Kind);
        return (true, $"Вход выполнен: {info.Str("login")}");
    }

    public async Task<JsonNode> OAuthToken(Dictionary<string, string> data, TwitchAccount acc = null)
    {
        if (acc != null && acc.IsPublic)
        {
            data["client_id"] = acc.ClientId;   // публичное приложение: без секрета
        }
        else
        {
            data["client_id"] = ClientId;
            data["client_secret"] = ClientSecret;
        }
        using var resp = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(data));
        var js = SafeParse(await resp.Content.ReadAsStringAsync());
        if (!resp.IsSuccessStatusCode)
            throw new TwitchException((int)resp.StatusCode, js.Str("message", "ошибка OAuth"));
        return js;
    }

    // ---------- вход по коду (Device Code Flow) ----------
    public sealed class DeviceLogin
    {
        public string Kind = "", UserCode = "", Uri = "", DeviceCode = "";
        public string Status = "waiting";    // waiting | ok | error | expired | cancelled
        public string Message = "";
        public double Expires;
        public int Interval = 5;
    }

    public DeviceLogin Device { get; private set; }

    public object DeviceState() => Device == null ? new { status = "none" } : new
    {
        kind = Device.Kind, code = Device.UserCode, uri = Device.Uri, status = Device.Status, message = Device.Message,
        left = Math.Max(0, (int)(Device.Expires - ActionLog.Now)),
    };

    /// <summary>Начинает вход по коду: Twitch выдаёт код, пользователь подтверждает его на twitch.tv/activate.</summary>
    public async Task<DeviceLogin> StartDevice(string kind)
    {
        var cid = PublicClientId;
        if (cid == "") throw new UserError("Быстрый вход недоступен в этой сборке — используйте свои ключи");
        if (Device != null && Device.Status == "waiting") Device.Status = "cancelled";
        var scopes = string.Join(" ", kind == "streamer" ? StreamerScopes : BotScopes);
        using var resp = await Http.PostAsync(DeviceUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = cid, ["scopes"] = scopes,
        }));
        var js = SafeParse(await resp.Content.ReadAsStringAsync());
        if (!resp.IsSuccessStatusCode)
            throw new UserError("Twitch не выдал код входа: " + js.Str("message", resp.ReasonPhrase ?? "ошибка"));
        var d = new DeviceLogin
        {
            Kind = kind, UserCode = js.Str("user_code"), Uri = js.Str("verification_uri"), DeviceCode = js.Str("device_code"),
            Expires = ActionLog.Now + js.Num("expires_in", 1800), Interval = Math.Max(2, js.Int("interval", 5)),
        };
        Device = d;
        _hub.Loop.Start(() => PollDevice(d, cid, scopes));
        return d;
    }

    public void CancelDevice()
    {
        if (Device != null && Device.Status == "waiting") Device.Status = "cancelled";
    }

    private async Task PollDevice(DeviceLogin d, string cid, string scopes)
    {
        while (d.Status == "waiting")
        {
            await Task.Delay(d.Interval * 1000);
            if (d.Status != "waiting") return;
            if (ActionLog.Now > d.Expires) { d.Status = "expired"; d.Message = "Код устарел — начните вход заново"; return; }
            JsonNode js;
            HttpResponseMessage resp;
            try
            {
                resp = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = cid, ["scopes"] = scopes, ["device_code"] = d.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }));
                js = SafeParse(await resp.Content.ReadAsStringAsync());
            }
            catch (Exception) { continue; }   // временный сбой сети — пробуем ещё
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var msg = js.Str("message").ToLowerInvariant();
                    if (msg.Contains("authorization_pending")) continue;
                    if (msg.Contains("slow_down")) { d.Interval += 5; continue; }
                    d.Status = msg.Contains("expired") || msg.Contains("invalid device") ? "expired" : "error";
                    d.Message = msg.Contains("denied") ? "Доступ отклонён на Twitch" :
                                d.Status == "expired" ? "Код устарел — начните вход заново" : "Twitch: " + js.Str("message");
                    return;
                }
            }
            try
            {
                var info = await Validate(js.Str("access_token"));
                if (d.Kind == "bot")
                {
                    var login = info.Str("login").ToLowerInvariant();
                    var expected = Cfg.Str("bot_login").Trim().TrimStart('@').ToLowerInvariant();
                    if (expected != "" && login != expected)
                    {
                        d.Status = "error";
                        d.Message = $"Подтверждено аккаунтом {info.Str("login")}, а бот — {expected}. Откройте ссылку в браузере, где вы вошли как бот (или в режиме инкогнито).";
                        return;
                    }
                    if (expected == "" && Streamer.Ready && login == Streamer.Login.ToLowerInvariant())
                    {
                        d.Status = "error";
                        d.Message = "Это аккаунт стримера. Для бота откройте ссылку в браузере, где вы вошли аккаунтом бота (или в режиме инкогнито).";
                        return;
                    }
                    if (expected == "") _hub.S.Set("twitch.bot_login", info.Str("login"));
                    _hub.S.Set("twitch.sender", "bot");
                    _hub.Settings.Save();
                }
                var acc = d.Kind == "streamer" ? Streamer : Bot;
                acc.SetTokens(js, info, isPublic: true);
                d.Status = "ok";
                d.Message = $"Вход выполнен: {info.Str("login")}";
                _hub.Log.Add("system", $"Вход Twitch ({(d.Kind == "streamer" ? "стример" : "бот")}): {info.Str("login")}");
                _hub.OnAccountChanged(d.Kind);
            }
            catch (Exception e)
            {
                d.Status = "error";
                d.Message = "Не удалось завершить вход: " + e.Message;
            }
            return;
        }
    }

    public async Task<JsonNode> Validate(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ValidateUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
        using var resp = await Http.SendAsync(req);
        var js = SafeParse(await resp.Content.ReadAsStringAsync());
        if (!resp.IsSuccessStatusCode)
            throw new TwitchException((int)resp.StatusCode, js.Str("message", "токен недействителен"));
        return js;
    }

    public static JsonNode SafeParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try { return JsonNode.Parse(text) ?? new JsonObject(); }
        catch { return new JsonObject { ["message"] = text.Length > 200 ? text[..200] : text }; }
    }

    // ---------- Helix ----------
    public async Task<JsonNode> Api(HttpMethod method, string path, Dictionary<string, string> query = null,
        object body = null, TwitchAccount account = null)
    {
        var acc = account ?? Streamer;
        var url = Helix + path;
        if (query != null && query.Count > 0)
            url += "?" + string.Join("&", query.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value ?? "")));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await acc.Token(attempt == 1);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Client-Id", acc.ClientId);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null)
                req.Content = new StringContent(J.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            var status = (int)resp.StatusCode;
            if (status == 401 && attempt == 0) continue;
            if (status == 204) return new JsonObject();
            var js = SafeParse(await resp.Content.ReadAsStringAsync());
            if (status >= 400) throw new TwitchException(status, js.Str("message", resp.ReasonPhrase ?? "ошибка API"));
            return js;
        }
        throw new TwitchException(401, "доступ отклонён");
    }

    public async Task<bool> SendChat(string text, string replyTo = null)
    {
        text = (text ?? "").Trim();
        if (text == "") return false;
        if (!Cfg.Bool("allow_send"))
        {
            _hub.Log.Add("skip", "Отправка сообщений в чат выключена в настройках");
            return false;
        }
        if (!SenderReady())
        {
            _hub.Log.Add("skip", "Отправитель сообщений не подключён");
            return false;
        }
        for (var i = 0; i < text.Length; i += 500)
        {
            var chunk = text.Substring(i, Math.Min(500, text.Length - i));
            await _sendLock.WaitAsync();
            try
            {
                var wait = 0.4 - (ActionLog.Now - _lastSend);
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                var body = new Dictionary<string, object>
                {
                    ["broadcaster_id"] = BroadcasterId, ["sender_id"] = Sender.UserId, ["message"] = chunk,
                };
                if (!string.IsNullOrEmpty(replyTo)) body["reply_parent_message_id"] = replyTo;
                JsonNode js;
                try
                {
                    js = await Api(HttpMethod.Post, "/chat/messages", body: body, account: Sender);
                }
                catch (Exception e) when (e is TwitchException or HttpRequestException or TaskCanceledException)
                {
                    LastSendError = e.Message;
                    _hub.Log.Error($"Ошибка отправки в чат: {e.Message}");
                    return false;
                }
                finally { _lastSend = ActionLog.Now; }
                var d = (js["data"] as JsonArray)?.FirstOrDefault() ?? new JsonObject();
                var mid = d.Str("message_id");
                if (mid != "")
                {
                    SentIds.AddLast(mid);
                    while (SentIds.Count > 300) SentIds.RemoveFirst();
                }
                if (!d.Bool("is_sent", true))
                {
                    var reason = d.Str("drop_reason.message", "сообщение отклонено Twitch");
                    LastSendError = reason;
                    _hub.Log.Error($"Twitch не опубликовал сообщение: {reason}");
                    return false;
                }
            }
            finally { _sendLock.Release(); }
        }
        LastSendError = "";
        return true;
    }

    // ---------- запросы данных ----------
    private static JsonNode First(JsonNode js) => (js["data"] as JsonArray)?.FirstOrDefault();

    public async Task<JsonNode> GetStream() =>
        First(await Api(HttpMethod.Get, "/streams", new() { ["user_id"] = BroadcasterId }));

    public async Task<JsonNode> GetChannel() =>
        First(await Api(HttpMethod.Get, "/channels", new() { ["broadcaster_id"] = BroadcasterId })) ?? new JsonObject();

    public async Task<string> GetFollowedAt(string userId)
    {
        var d = First(await Api(HttpMethod.Get, "/channels/followers",
            new() { ["broadcaster_id"] = BroadcasterId, ["user_id"] = userId }));
        return d?.Str("followed_at");
    }

    public async Task<bool> IsVip(string userId) =>
        First(await Api(HttpMethod.Get, "/channels/vips", new() { ["broadcaster_id"] = BroadcasterId, ["user_id"] = userId })) != null;

    public async Task<bool> IsSubscriber(string userId) =>
        First(await Api(HttpMethod.Get, "/subscriptions", new() { ["broadcaster_id"] = BroadcasterId, ["user_id"] = userId })) != null;

    public async Task<List<JsonNode>> ListRewards()
    {
        var js = await Api(HttpMethod.Get, "/channel_points/custom_rewards",
            new() { ["broadcaster_id"] = BroadcasterId, ["only_manageable_rewards"] = "true" });
        return (js["data"] as JsonArray)?.ToList() ?? new List<JsonNode>();
    }

    public async Task<JsonNode> CreateReward(object body) =>
        First(await Api(HttpMethod.Post, "/channel_points/custom_rewards", new() { ["broadcaster_id"] = BroadcasterId }, body));

    public async Task UpdateReward(string id, object body) =>
        await Api(HttpMethod.Patch, "/channel_points/custom_rewards", new() { ["broadcaster_id"] = BroadcasterId, ["id"] = id }, body);

    public async Task DeleteReward(string id) =>
        await Api(HttpMethod.Delete, "/channel_points/custom_rewards", new() { ["broadcaster_id"] = BroadcasterId, ["id"] = id });

    public async Task SetRedemption(string rewardId, string redemptionId, string status) =>
        await Api(HttpMethod.Patch, "/channel_points/custom_rewards/redemptions",
            new() { ["broadcaster_id"] = BroadcasterId, ["reward_id"] = rewardId, ["id"] = redemptionId },
            new { status });
}

/// <summary>Ошибка, текст которой показывается пользователю в панели (аналог ValueError в Python-версии).</summary>
public sealed class UserError : Exception
{
    /// <summary>Путь к полю настроек (например, music.queue_limit) — панель подсветит его.</summary>
    public string Field { get; }

    public UserError(string message, string field = null) : base(message) { Field = field; }
}
