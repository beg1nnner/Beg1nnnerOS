using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Core;

/// <summary>Резервные копии конфигурации (без токенов, истории и секретов) — раздел 16.</summary>
public static class Backup
{
    private static readonly string[] ConfigFiles = { "settings.json", "commands.json", "timers.json", "alerts.json" };
    private static readonly (string Sec, string Key)[] Secrets =
        { ("twitch", "client_secret"), ("yandex", "token"), ("donationalerts", "token"), ("donationalerts", "client_secret"),
          ("donationalerts", "refresh_token"), ("announce", "tg_token"), ("announce", "dc_webhook"), ("tools", "translate.api_key"), ("pro", "tts.yandex_key"), ("pro", "cloud.password") };

    private static void ClearSecrets(JsonNode settings)
    {
        foreach (var (sec, key) in Secrets)
            if (settings?[sec] is JsonObject o && o.At(key) != null) o.Set(key, "");
    }

    public static string Create(string reason = "manual")
    {
        var name = $"backup-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.zip";
        var used = new HashSet<string>();
        try
        {
            var alerts = JsonNode.Parse(File.ReadAllText(Path.Combine(Config.Profile, "alerts.json")));
            foreach (var a in alerts?["items"] as JsonArray ?? new JsonArray())
                foreach (var f in new[] { a.Str("media"), a.Str("sound") })
                    if (f != "") used.Add(f);
        }
        catch { }
        using var zip = ZipFile.Open(Path.Combine(Config.BackupDir, name), ZipArchiveMode.Create);
        foreach (var f in ConfigFiles)
        {
            var p = Path.Combine(Config.Profile, f);
            if (!File.Exists(p)) continue;
            if (f == "settings.json")
            {
                var data = JsonNode.Parse(File.ReadAllText(p));
                ClearSecrets(data);   // секреты в копию не входят
                using var w = new StreamWriter(zip.CreateEntry(f).Open());
                w.Write(data.ToJsonString(J.Pretty));
            }
            else zip.CreateEntryFromFile(p, f);
        }
        foreach (var m in used)
        {
            var p = Path.Combine(Config.MediaDir, m);
            if (File.Exists(p)) zip.CreateEntryFromFile(p, "media/" + m);
        }
        return name;
    }

    public static List<object> List() =>
        new DirectoryInfo(Config.BackupDir).GetFiles("*.zip").OrderByDescending(f => f.Name)
            .Select(f => (object)new { name = f.Name, size = f.Length, ts = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds() })
            .ToList();

    public static void Restore(string name)
    {
        var path = Path.Combine(Config.BackupDir, Path.GetFileName(name));
        if (!File.Exists(path)) throw new UserError("Копия не найдена");
        Create("before-restore");
        using var zip = ZipFile.OpenRead(path);
        foreach (var e in zip.Entries)
        {
            if (ConfigFiles.Contains(e.FullName))
            {
                using var r = new StreamReader(e.Open());
                var text = r.ReadToEnd();
                if (e.FullName == "settings.json")
                {
                    // Секреты текущего профиля сохраняются
                    var data = JsonNode.Parse(text);
                    try
                    {
                        var cur = JsonNode.Parse(File.ReadAllText(Path.Combine(Config.Profile, "settings.json")));
                        foreach (var (sec, key) in Secrets)
                            if (data?[sec] is JsonObject o) o.Set(key, J.AsStr(cur?[sec]?.At(key)));
                    }
                    catch { }
                    text = data.ToJsonString(J.Pretty);
                }
                File.WriteAllText(Path.Combine(Config.Profile, e.FullName), text);
            }
            else if (e.FullName.StartsWith("media/") && e.Name != "" && !e.FullName[6..].Contains('/'))
                e.ExtractToFile(Path.Combine(Config.MediaDir, e.Name), true);
        }
    }
}
