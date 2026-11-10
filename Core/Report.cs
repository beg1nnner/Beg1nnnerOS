using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beg1nnnerOS.Core;

/// <summary>
/// «Сохранить отчёт об ошибке»: один zip-файл для разработчика — журнал, версия, состояние подключений
/// и настройки. Пароли, токены и ключи вырезаются, файл можно смело отправлять.
/// </summary>
public static class Report
{
    // Любой ключ настроек с такими словами считается секретом и в отчёт не попадает
    private static readonly Regex SecretKey = new("token|secret|password|key|cookie|refresh|auth|folder_id|webhook",
        RegexOptions.IgnoreCase);

    public static string Create(Hub hub)
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Config.Profile;
        var path = Path.Combine(dir, $"Beg1nnnerOS-отчёт-{DateTime.Now:yyyy-MM-dd_HH-mm}.zip");

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, "README.txt",
                "Отчёт Beg1nnnerOS для разработчика.\r\n" +
                "Пароли, токены и ключи удалены. Отправьте этот файл в Telegram: t.me/Beg1nnner\r\n");
            Add(zip, "info.txt", Info(hub));
            Add(zip, "status.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                ready = hub.Readiness(),
                music = hub.Music.State(),
                update = hub.Updater.State(),
                ytdlp = hub.YtDlp.Status,
            }, J.Pretty));
            Add(zip, "settings.json", Redact(hub.S.DeepClone()).ToJsonString(J.Pretty));

            // журнал: текущий файл + предыдущий (если был перенос по размеру), последние ~1 МБ
            foreach (var name in new[] { "application.log.1", "application.log" })
            {
                var f = Path.Combine(Config.Profile, name);
                if (!File.Exists(f)) continue;
                try
                {
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (fs.Length > 1_000_000) fs.Seek(-1_000_000, SeekOrigin.End);
                    using var r = new StreamReader(fs, Encoding.UTF8);
                    Add(zip, name.Replace(".log.1", "-previous.log"), MaskInText(r.ReadToEnd()));
                }
                catch { }
            }
            var mem = string.Join("\r\n", hub.Log.Tail(500).Select(i =>
                $"{DateTimeOffset.FromUnixTimeMilliseconds((long)(i.ts * 1000)).ToLocalTime():HH:mm:ss} [{i.kind}] {i.text}"));
            Add(zip, "journal-now.txt", MaskInText(mem));
        }
        hub.Log.Add("system", $"Отчёт об ошибке сохранён: {path}");
        return path;
    }

    private static string Info(Hub hub)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Версия: {Config.Version}");
        sb.AppendLine($"Создан: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (UTC{DateTimeOffset.Now:zzz})");
        sb.AppendLine($"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Профиль: {(Config.ProfileName == "" ? "основной" : Config.ProfileName)}");
        sb.AppendLine($"Вход стримера: {(hub.Twitch.Streamer.Ready ? "есть" + (hub.Twitch.Streamer.IsPublic ? " (по коду)" : " (свои ключи)") : "нет")}");
        sb.AppendLine($"Вход бота: {(hub.Twitch.Bot.Ready ? "есть" : "нет")}");
        sb.AppendLine($"Чат и события (EventSub): {(hub.EventSub.Connected ? "подключено" : "нет соединения")}");
        sb.AppendLine($"Источник /alerts в OBS: {hub.Alerts.Clients.Count}");
        sb.AppendLine($"yt-dlp: {hub.YtDlp.Status}");
        return sb.ToString();
    }

    /// <summary>Заменяет значения секретных полей на «***» (с сохранением признака «было заполнено»).</summary>
    private static JsonNode Redact(JsonNode n)
    {
        if (n is JsonObject o)
        {
            foreach (var key in o.Select(p => p.Key).ToList())
            {
                var v = o[key];
                if (SecretKey.IsMatch(key) && v is JsonValue)
                    o[key] = J.AsStr(v) == "" ? "" : "*** (заполнено)";
                else Redact(v);
            }
        }
        else if (n is JsonArray a)
            foreach (var x in a) Redact(x);
        return n;
    }

    /// <summary>На всякий случай вычищает похожие на токены строки из текста журнала.</summary>
    private static string MaskInText(string s) =>
        Regex.Replace(s, @"(?i)(oauth:|bearer\s+|token[=:]\s*|secret[=:]\s*)[A-Za-z0-9_\-\.]{12,}", "$1***");

    private static void Add(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(true));
        w.Write(text);
    }
}
