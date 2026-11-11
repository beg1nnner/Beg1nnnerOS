using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Features;
using Beg1nnnerOS.Pro;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Core;

/// <summary>Центральный объект: связывает модули и запускает фоновые задачи в HubLoop.</summary>
public sealed class Hub
{
    public readonly double Started = ActionLog.Now;
    public readonly HubLoop Loop = new();
    public readonly HttpClient Http;
    public readonly JsonStore Settings;
    public readonly JsonStore State;
    public readonly ActionLog Log;
    public readonly Stats Stats;
    public readonly TwitchClient Twitch;
    public readonly StreamState Stream;
    public readonly Commands Commands;
    public readonly Timers Timers;
    public readonly Events Events;
    public readonly Music Music;
    public readonly Alerts Alerts;
    public readonly Rewards Rewards;
    public readonly EventSub EventSub;
    public readonly YandexMusic Yandex;
    public readonly ShazamTool Shazam;
    public readonly Translator Translator;
    public readonly DonationAlerts Donations;
    public readonly YtDlp YtDlp;
    public readonly ChatOverlay ChatOverlay;
    public readonly ProHub Pro;
    public readonly Updater Updater;
    public readonly Features.Announce Announce;

    /// <summary>Перезапуск программы (смена профиля, восстановление копии) — задаётся окном.</summary>
    public Action RestartApp = () => { };

    /// <summary>Закрытие программы (например, перед установкой обновления) — задаётся окном.</summary>
    public Action ExitApp = () => { };

    /// <summary>Найдена новая версия (номер) — окно показывает уведомление Windows и пункт в меню трея.</summary>
    public Action<string> UpdateFound = _ => { };

    /// <summary>Когда оверлей плеера в OBS последний раз запрашивал данные.</summary>
    public DateTime PlayerOverlaySeen = DateTime.MinValue;

    /// <summary>Действия рабочего стола (горячие клавиши/трей) — задаются окном.</summary>
    public Func<Dictionary<string, string>> HotkeyStatus = () => new Dictionary<string, string>();
    public string TrayStatus = "выключен";
    public Action ApplyHotkeys = () => { };

    public JsonObject S => Settings.Data;

    public Hub()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Beg1nnnerOS/{Config.Version}");
        Settings = new JsonStore(Path.Combine(Config.Profile, "settings.json"), Config.DefaultSettings());
        State = new JsonStore(Path.Combine(Config.Profile, "state.json"),
            new JsonObject { ["announced_stream"] = "", ["autopost_stream"] = "", ["reward_ids"] = new JsonObject() });
        Log = new ActionLog(Config.Profile);
        Loop.OnError = e => Log.Error($"Фоновая задача: {e.GetType().Name}: {e.Message}");
        Stats = new Stats(Path.Combine(Config.Profile, "stats.sqlite"));
        Twitch = new TwitchClient(this);
        Stream = new StreamState(this);
        Commands = new Commands(this);
        Timers = new Timers(this);
        Events = new Events(this);
        Music = new Music(this);
        Alerts = new Alerts(this);
        Rewards = new Rewards(this);
        EventSub = new EventSub(this);
        Yandex = new YandexMusic(this);
        Shazam = new ShazamTool(this);
        Translator = new Translator(this);
        Donations = new DonationAlerts(this);
        YtDlp = new YtDlp(this);
        ChatOverlay = new ChatOverlay(this);
        Pro = new ProHub(this);
        Updater = new Updater(this);
        Announce = new Features.Announce(this);
    }

    public void Start()
    {
        Log.Add("system", $"Beg1nnnerOS {Config.Version} запущен. Профиль: {(Config.ProfileName == "" ? Profiles.MainTitle : Config.ProfileName)} ({Config.Profile})");
        Loop.Start(EventSub.Run);
        Loop.Start(Stream.Run);
        Loop.Start(Timers.Run);
        Loop.Start(Events.Run);
        Loop.Start(Music.Run);
        Loop.Start(Alerts.Run);
        Loop.Start(Donations.Run);
        Loop.Start(YtDlp.UpdateIfNeeded);
        Loop.Start(Updater.Run);
        Pro.Start();
        Loop.Start(async () =>
        {
            if (!Twitch.Streamer.Ready) return;
            await ChatOverlay.LoadBadges();
            await Rewards.Sync();
        });
    }

    public void Stop()
    {
        try { Loop.CallSync(() => { Pro.Cloud.SaveOnExit(); Pro.Tts.Clear(); Music.Player.Stop(); return true; }).Wait(8000); } catch { }
    }

    public void OnAccountChanged(string kind)
    {
        if (kind != "streamer") return;
        EventSub.Restart();
        Stream.Checked = false;
        Loop.Start(() => Stream.Refresh(true));
        Rewards.ScheduleSync();
        Loop.Start(ChatOverlay.LoadBadges);
    }

    // ---------- события EventSub ----------
    public async Task OnEventSub(string stype, JsonNode ev)
    {
        if (stype == "channel.chat.message") await OnChat(ev);
        else if (stype is "channel.chat.message_delete" or "channel.chat.clear_user_messages" or "channel.chat.clear")
            await ChatOverlay.Handle(stype, ev);
        else if (stype.StartsWith("stream.") || stype == "channel.update") await Stream.Handle(stype, ev);
        else if (stype == "channel.channel_points_custom_reward_redemption.add") await Rewards.OnRedemption(ev);
        else await Events.Handle(stype, ev);
    }

    public async Task OnChat(JsonNode ev)
    {
        var mid = ev.Str("message_id");
        var uid = ev.Str("chatter_user_id");
        // В OBS показываем все сообщения чата, включая ответы программы (их можно скрыть в настройках)
        var own = Twitch.SentIds.Contains(mid) || (S.Str("twitch.sender") == "bot" && Twitch.Bot.Ready && uid == Twitch.Bot.UserId);
        ChatOverlay.OnMessage(ev, own);
        // Ответы приложения не запускают команды повторно; в режиме бота его сообщения игнорируются
        if (Twitch.SentIds.Contains(mid)) return;
        if (S.Str("twitch.sender") == "bot" && Twitch.Bot.Ready && uid == Twitch.Bot.UserId) return;
        var text = ev.Str("message.text");
        var reply = ev["reply"] as JsonObject;
        if (reply != null)
        {
            // Ответ в Twitch начинается с «@ник » — убираем, чтобы команда в ответе работала
            var prefix = "@" + reply.Str("parent_user_login");
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..].TrimStart();
        }
        var badges = new HashSet<string>((ev["badges"] as JsonArray ?? new JsonArray()).Select(b => b.Str("set_id")));
        var msg = new ChatMsg
        {
            UserId = uid, Login = ev.Str("chatter_user_login"), Name = ev.Str("chatter_user_name"), Text = text,
            MessageId = mid, Badges = badges, ReplyBody = reply.Str("parent_message_body"), ReplyUser = reply.Str("parent_user_name"),
        };
        Stats.Chat(msg.Login, msg.Name, Stream.Key);
        Timers.OnChatMessage();
        if (await Pro.OnChat(msg)) return;   // розыгрыш, мини-игры, !tts
        if (msg.Text.StartsWith("!"))
        {
            await Commands.Handle(msg);
            return;
        }
        await ChatTools(msg);
    }

    /// <summary>Автоисправление раскладки и автоперевод обычных сообщений.</summary>
    public async Task ChatTools(ChatMsg msg)
    {
        if (S.Bool("tools.layout.enabled"))
        {
            var fixedText = Layout.Fix(msg.Text);
            if (fixedText != null)
            {
                Log.Add("command", $"Раскладка: {msg.Login}: {msg.Text} → {fixedText}");
                await Twitch.SendChat($"{msg.Name}: {fixedText}");
                return;
            }
        }
        if (Translator.WantsAuto(msg.Text))
        {
            try
            {
                var (outText, src) = await Translator.Translate(msg.Text);
                if (outText.Trim() != "" && !string.Equals(outText.Trim(), msg.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    await Twitch.SendChat($"{msg.Name} [{src}]: {outText}");
            }
            catch (Exception e) { Log.Add("skip", $"Автоперевод: {e.Message}"); }
        }
    }

    // ---------- сводка для «Эфира» ----------
    public object Readiness()
    {
        var tw = Twitch;
        var sender = tw.Sender;
        return new
        {
            keys = Twitch.OwnKeys || Twitch.QuickLoginAvailable || Twitch.Streamer.IsPublic,
            quick_login = Twitch.QuickLoginAvailable,
            streamer = new
            {
                ok = tw.Streamer.Ready, login = tw.Streamer.Login, problem = tw.Streamer.Problem,
                missing_scopes = tw.Streamer.Ready ? tw.Streamer.MissingScopes() : new List<string>(),
            },
            bot = new { ok = tw.Bot.Ready, login = tw.Bot.Login },
            sender = new
            {
                mode = S.Str("twitch.sender"), ok = tw.SenderReady(), login = sender.Login,
                error = tw.LastSendError, allow = S.Bool("twitch.allow_send"),
            },
            eventsub = new { ok = EventSub.Connected, failed = EventSub.Failed },
            alerts_source = Alerts.Clients.Count,
            obs = new
            {
                player = (DateTime.UtcNow - PlayerOverlaySeen).TotalSeconds < 6,
                chat = ChatOverlay.Clients.Count(c => c.Obs),
                alerts = Alerts.Clients.Count(c => c.Obs),
                music = Music.ObsConnected,
            },
            wizard_done = S.Bool("ui.wizard_done"),
            donationalerts = new { ok = Donations.Connected, status = Donations.Status, configured = Donations.Token != "" },
            rewards = Rewards.Status,
            stream = new { live = Stream.Live, title = Stream.Title, category = Stream.Category, uptime = Stream.UptimeText() },
        };
    }
}
