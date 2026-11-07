using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>Чат в OBS: последние сообщения, значки Twitch, удаление сообщений модерацией.</summary>
public sealed class ChatOverlay
{
    private const int Keep = 60;
    private readonly Hub _hub;
    public readonly LinkedList<JsonObject> History = new();
    public readonly HashSet<AlertClient> Clients = new();
    private Dictionary<string, string> _badges = new();   // "set_id/id" → картинка 2x
    private string _badgesFor = "";

    public ChatOverlay(Hub hub) { _hub = hub; }

    // ---------- значки ----------
    public async Task LoadBadges()
    {
        var tw = _hub.Twitch;
        if (!tw.Streamer.Ready || _badgesFor == tw.BroadcasterId) return;
        var found = new Dictionary<string, string>();
        try
        {
            foreach (var (path, query) in new (string, Dictionary<string, string>)[]
                     {
                         ("/chat/badges/global", null),
                         ("/chat/badges", new Dictionary<string, string> { ["broadcaster_id"] = tw.BroadcasterId }),
                     })
            {
                var js = await tw.Api(HttpMethod.Get, path, query);
                foreach (var set in js["data"] as JsonArray ?? new JsonArray())
                    foreach (var v in set["versions"] as JsonArray ?? new JsonArray())
                    {
                        var url = v.Str("image_url_2x");
                        found[$"{set.Str("set_id")}/{v.Str("id")}"] = url != "" ? url : v.Str("image_url_1x");
                    }
            }
        }
        catch (TwitchException e)
        {
            _hub.Log.Add("skip", $"Значки чата не загружены: {e.Reason}");
            return;
        }
        _badges = found;
        _badgesFor = tw.BroadcasterId;
    }

    // ---------- сообщения ----------
    private JsonObject Build(JsonNode ev, bool own)
    {
        var frags = new JsonArray();
        var fragments = ev.At("message.fragments") as JsonArray;
        if (fragments == null || fragments.Count == 0)
            frags.Add(new JsonObject { ["t"] = "text", ["text"] = ev.Str("message.text") });
        else
            foreach (var f in fragments)
            {
                var type = f.Str("type");
                var emoteId = f.Str("emote.id");
                if (type == "emote" && emoteId != "")
                    frags.Add(new JsonObject
                    {
                        ["t"] = "emote", ["name"] = f.Str("text"),
                        ["url"] = $"https://static-cdn.jtvnw.net/emoticons/v2/{emoteId}/default/dark/2.0",
                    });
                else if (type == "mention")
                    frags.Add(new JsonObject { ["t"] = "mention", ["text"] = f.Str("text") });
                else
                    frags.Add(new JsonObject { ["t"] = "text", ["text"] = f.Str("text") });
            }
        var badges = new JsonArray();
        foreach (var b in ev["badges"] as JsonArray ?? new JsonArray())
            if (_badges.TryGetValue($"{b.Str("set_id")}/{b.Str("id")}", out var url)) badges.Add(url);
        return new JsonObject
        {
            ["type"] = "msg", ["id"] = ev.Str("message_id"), ["user_id"] = ev.Str("chatter_user_id"),
            ["login"] = ev.Str("chatter_user_login"), ["name"] = ev.Str("chatter_user_name"),
            ["color"] = ev.Str("color"), ["badges"] = badges, ["fragments"] = frags,
            ["text"] = ev.Str("message.text"), ["own"] = own, ["ts"] = ActionLog.Now,
        };
    }

    public void OnMessage(JsonNode ev, bool own)
    {
        var item = Build(ev, own);
        History.AddLast(item);
        while (History.Count > Keep) History.RemoveFirst();
        Broadcast(item);
    }

    private void RemoveWhere(Func<JsonObject, bool> pred)
    {
        var node = History.First;
        while (node != null)
        {
            var next = node.Next;
            if (pred(node.Value)) History.Remove(node);
            node = next;
        }
    }

    public Task Handle(string stype, JsonNode ev)
    {
        switch (stype)
        {
            case "channel.chat.message_delete":
                var id = ev.Str("message_id");
                RemoveWhere(m => m.Str("id") == id);
                Broadcast(new JsonObject { ["type"] = "delete", ["id"] = id });
                break;
            case "channel.chat.clear_user_messages":
                var uid = ev.Str("target_user_id");
                RemoveWhere(m => m.Str("user_id") == uid);
                Broadcast(new JsonObject { ["type"] = "clear_user", ["user_id"] = uid });
                break;
            case "channel.chat.clear":
                History.Clear();
                Broadcast(new JsonObject { ["type"] = "clear" });
                break;
        }
        return Task.CompletedTask;
    }

    // ---------- рассылка ----------
    public void Broadcast(JsonObject msg)
    {
        var json = msg.ToJsonString(J.Options);
        foreach (var c in Clients.ToList()) _ = SendTo(c, json);
    }

    private async Task SendTo(AlertClient c, string json)
    {
        if (!await c.Send(json)) Clients.Remove(c);
    }

    public string HistoryJson()
    {
        var items = new JsonArray();
        foreach (var m in History) items.Add(m.DeepClone());
        return new JsonObject { ["type"] = "history", ["items"] = items }.ToJsonString(J.Options);
    }

    public object Config() => new
    {
        settings = _hub.S["chat_overlay"]?.DeepClone(),
        broadcaster_id = _hub.Twitch.BroadcasterId,
        channel = _hub.Twitch.Streamer.Login,
        custom_themes = _hub.Pro.ThemesFor("chat"),
    };
}
