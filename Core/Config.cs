using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Beg1nnnerOS.Core;

public sealed record EventType(string Key, string Title, string[] Vars, string Template, bool Enabled);

public static class Config
{
    public const string AppName = "Beg1nnnerOS";
    public const string Version = "2.5.0";
    public const string RedirectUri = "http://localhost:4343/callback";
    public const int CallbackPort = 4343;

    /// <summary>Общая папка программы: профили, инструменты.</summary>
    public static readonly string Root = InitRoot();
    /// <summary>Имя активного профиля ("" — основной профиль в корне).</summary>
    public static readonly string ProfileName = Profiles.ActiveName(Root);
    /// <summary>Папка данных активного профиля: настройки, входы, команды, статистика.</summary>
    public static readonly string Profile = InitProfile();
    public static readonly string MediaDir = Path.Combine(Profile, "media");
    public static readonly string BackupDir = Path.Combine(Profile, "backups");
    public static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "Beg1nnnerOS-audio");
    public static readonly string WebDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    private static string InitRoot()
    {
        var custom = Environment.GetEnvironmentVariable("HUB_PROFILE");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var p = !string.IsNullOrEmpty(custom) ? custom : Path.Combine(local, AppName);
        if (string.IsNullOrEmpty(custom) && !Directory.Exists(p))
        {
            // Перенос профиля из прежнего названия программы — настройки и входы сохраняются
            var old = Path.Combine(local, "ArsappyHubCore");
            if (Directory.Exists(old))
                try { CopyDir(old, p); } catch { }
        }
        Directory.CreateDirectory(p);
        return p;
    }

    private static string InitProfile()
    {
        var p = Profiles.PathFor(Root, ProfileName);
        foreach (var sub in new[] { "", "media", "backups" })
            Directory.CreateDirectory(Path.Combine(p, sub));
        return p;
    }

    public static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // Сообщения о событиях (раздел 11 руководства)
    public static readonly List<EventType> EventTypes = new()
    {
        new("donation", "Донат DonationAlerts", new[] { "user", "amount", "currency", "message" },
            "{user} задонатил(а) {amount} {currency}! {message}", true),
        new("follow", "Новый фолловер", new[] { "user" }, "Спасибо за отслеживание, {user}! 💜", true),
        new("sub", "Подписка или подарки", new[] { "user", "details", "months", "count", "tier" },
            "{user} {details}! Спасибо за поддержку 💜", true),
        new("cheer", "Cheer / Bits", new[] { "user", "bits", "message" }, "{user} отправил(а) {bits} bits! {message}", true),
        new("reward", "Награда за баллы Twitch", new[] { "user", "reward", "cost" },
            "{user} получил(а) награду «{reward}» за {cost} баллов", false),
        new("raid", "Входящий рейд", new[] { "user", "viewers" }, "Рейд от {user}: {viewers} зрителей! Добро пожаловать!", true),
        new("hype", "Hype Train", new[] { "event", "level", "details" }, "Hype Train {event}! Уровень {level}. {details}", true),
        new("poll", "Голосование", new[] { "event", "title", "details" }, "Голосование {event}: {title}. {details}", true),
        new("ad", "Рекламная вставка", new[] { "duration" }, "Начинается реклама на {duration} сек. Скоро вернёмся!", true),
        new("online", "Начало трансляции", new[] { "title", "category" }, "Начинаем! {title}. Категория: {category}", true),
        new("update", "Смена названия или категории", new[] { "title", "category" },
            "Обновление трансляции: {title}. Играем в {category}", true),
    };

    public static JsonObject DefaultSettings()
    {
        var s = (JsonObject)JsonNode.Parse(DefaultsJson);
        var templates = new JsonObject();
        foreach (var e in EventTypes)
            templates[e.Key] = new JsonObject { ["enabled"] = e.Enabled, ["text"] = e.Template };
        ((JsonObject)s["events"])["templates"] = templates;
        return s;
    }

    private const string DefaultsJson = """
    {
      "server": {"host": "127.0.0.1", "port": 8080, "open_browser": true},
      "twitch": {"client_id": "", "client_secret": "", "sender": "streamer", "bot_login": "", "allow_send": true},
      "chat": {"user_rate_limit_sec": 2},
      "autopost": {"enabled": false, "text": ""},
      "announce": {"tg_enabled": false, "tg_token": "", "tg_chat": "", "dc_enabled": false, "dc_webhook": "",
                   "text": "", "photo": true, "delay": 60, "summary_auto": false, "clips_post": false},
      "mod": {"enabled": false, "exempt_vip": true, "exempt_sub": false, "escalate": true, "escalate_min": 10, "notify": true,
              "warn_text": "@{user}, {reason}", "timeout_text": "{user} отдыхает {time}: {reason}",
              "links": {"enabled": true, "action": "delete", "timeout": 60, "permit_sec": 60,
                        "allow": ["twitch.tv", "youtube.com", "youtu.be", "music.yandex.ru", "open.spotify.com", "spotify.link"]},
              "words": {"enabled": false, "action": "timeout", "timeout": 300, "list": []},
              "repeat": {"enabled": true, "action": "delete", "timeout": 60, "count": 3, "window": 30},
              "caps": {"enabled": true, "action": "warn", "timeout": 30, "min_len": 15, "percent": 70},
              "symbols": {"enabled": true, "action": "delete", "timeout": 30, "run": 12, "emotes": 10},
              "length": {"enabled": false, "action": "delete", "timeout": 30, "max": 350}},
      "votes": {"chat_results": true, "chat_start": false, "chat_lock": true, "overlay_hide_sec": 20, "overlay_points": true,
                "poll_templates": [
                  {"title": "Во что играем дальше?", "choices": ["Продолжаем", "Другая игра", "Общение"], "duration": 120},
                  {"title": "Как вам трек?", "choices": ["🔥 Огонь", "👍 Норм", "👎 Не моё"], "duration": 60},
                  {"title": "Сложность?", "choices": ["Легко", "Нормально", "Хардкор"], "duration": 90}
                ],
                "pred_templates": [
                  {"title": "Выиграю этот раунд?", "outcomes": ["Да", "Нет"], "window": 120},
                  {"title": "Пройду босса с первой попытки?", "outcomes": ["Да", "Нет"], "window": 180},
                  {"title": "Сколько смертей будет?", "outcomes": ["0–2", "3–5", "6+"], "window": 120}
                ]},
      "music": {
        "rewards_enabled": true,
        "rewards": {
          "order": {"title": "Заказать музыку", "cost": 500, "enabled": true},
          "priority": {"title": "Трек вне очереди", "cost": 1500, "enabled": true},
          "skip": {"title": "Пропустить трек", "cost": 1000, "enabled": true}
        },
        "sources": {"youtube": true, "yandex": true, "spotify": true, "search": true},
        "search_first": "youtube",
        "per_user_limit": 2, "queue_limit": 15, "max_duration_min": 8, "min_views": 0, "banned_words": [],
        "vip_priority": false, "sub_priority": false, "skip_vote_enabled": true, "skip_votes": 3,
        "volume": 60, "audio_device": "", "announce": true, "blocked_users": [], "blocked_tracks": [],
        "order_sound": true, "order_sound_style": "bell", "order_sound_volume": 50, "chat_errors": true,
        "normalize": true, "normalize_target": -14, "output": "app",
        "fallback_enabled": false, "fallback_url": "", "fallback_shuffle": true, "fallback_interrupt": true, "fallback_liked": true,
        "restore_queue": true, "restore_hours": 12
      },
      "overlay": {
        "theme": "classic", "custom_colors": false, "width": 700, "height": 460,
        "accent": "#9146ff", "background": "rgba(16,16,22,0.78)", "text_color": "#ffffff",
        "font_size": 20, "show_queue": true, "queue_size": 5, "queue_position": "below", "show_requester": true,
        "show_progress": true, "toasts": true
      },
      "alerts": {"enabled": true, "volume": 80},
      "chat_overlay": {
        "theme": "classic", "font_size": 20, "max_messages": 20, "fade_sec": 0, "direction": "bottom",
        "show_badges": true, "hide_commands": true, "hide_app": false,
        "hidden_users": ["nightbot", "streamelements", "moobot", "streamlabs", "wizebot"],
        "emotes": {"7tv": true, "bttv": false, "ffz": false}
      },
      "events": {"interval_sec": 3, "gift_merge_sec": 5, "paused": false},
      "yandex": {"token": ""},
      "donationalerts": {"client_id": "", "client_secret": "", "token": "", "refresh_token": "", "expires_at": 0},
      "tools": {
        "shazam": {"enabled": false, "device": "", "interval": 5},
        "translate": {"enabled": false, "source": "auto", "target": "ru", "auto": false,
                      "url": "http://127.0.0.1:5000", "api_key": ""},
        "layout": {"enabled": false}
      },
      "hotkeys": {"enabled": false, "pause_music": "ctrl+alt+p", "skip": "ctrl+alt+right",
                  "stop_alerts": "ctrl+alt+backspace", "pause_events": "ctrl+alt+n",
                  "like_track": "ctrl+alt+l"},
      "ui": {"tray": true, "theme": "twitch", "close_action": "ask", "wizard_done": false},
      "pro": {
        "themes": [],
        "cloud": {"enabled": false, "folder": "", "include_secrets": false, "password": "", "auto": true},
        "games": {"enabled": false, "dice": true, "duel": true, "cooldown": 30, "duel_timeout": 60, "guess_max": 100},
        "giveaway": {"prize": "", "keyword": "!участвую", "reward_title": "", "minutes": 0, "subs_only": false,
                     "followers_only": false, "sub_luck": 2, "winners": 1}
      }
    }
    """;
}
