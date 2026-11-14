using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Pro;

/// <summary>
/// Облачная копия: программа сохраняет копию профиля в папку облака на компьютере
/// (Яндекс Диск, Google Drive, OneDrive, Dropbox) — клиент облака сам синхронизирует её на другие ПК.
/// Ключи и входы попадают в копию только по желанию и только зашифрованными паролем (AES-GCM, PBKDF2).
/// </summary>
public sealed class CloudSync
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("B1C1");
    private static readonly string[] Files = { "settings.json", "commands.json", "timers.json", "alerts.json" };
    private static readonly string[] TokenFiles = { "tokens.json", "bot_tokens.json" };
    /// <summary>Секретные поля настроек: в открытую часть копии не попадают.</summary>
    private static readonly string[] SecretPaths =
    {
        "twitch.client_secret", "yandex.token", "donationalerts.client_secret", "donationalerts.token",
        "donationalerts.refresh_token", "tools.translate.api_key",
    };

    private readonly Hub _hub;
    public string Status = "выключено";
    private string _lastHash = "";

    public CloudSync(Hub hub) { _hub = hub; }

    private JsonObject Cfg => _hub.S.Obj("pro.cloud");
    private static string ProfileTitle => Config.ProfileName == "" ? Profiles.MainTitle : Config.ProfileName;

    public string FilePath()
    {
        var folder = Cfg.Str("folder").Trim();
        if (folder == "") return "";
        return Path.Combine(folder, "Beg1nnnerOS", ProfileTitle + ".b1cloud");
    }

    private static List<object> _foldersCache;
    private static double _foldersAt;

    /// <summary>Папки облачных клиентов, найденные на этом компьютере (кэш на 5 минут).</summary>
    public static List<object> DetectFolders()
    {
        if (_foldersCache != null && ActionLog.Now - _foldersAt < 300) return _foldersCache;
        _foldersCache = FindFolders();
        _foldersAt = ActionLog.Now;
        return _foldersCache;
    }

    private static List<object> FindFolders()
    {
        var found = new List<(string Name, string Path)>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void Add(string name, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !found.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                found.Add((name, path));
        }
        Add("Яндекс Диск", Path.Combine(home, "YandexDisk"));
        Add("Яндекс Диск", Path.Combine(home, "Яндекс.Диск"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("Dropbox", Path.Combine(home, "Dropbox"));
        Add("Google Drive", Path.Combine(home, "Google Drive"));
        try
        {
            // Google Drive для компьютера подключается отдельным диском с папкой «Мой диск»
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                foreach (var sub in new[] { "Мой диск", "My Drive" })
                    Add("Google Drive", Path.Combine(d.RootDirectory.FullName, sub));
            }
        }
        catch { }
        return found.Select(f => (object)new { name = f.Name, path = f.Path }).ToList();
    }

    // ---------- шифрование ----------
    private static byte[] Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return Magic.Concat(salt).Concat(nonce).Concat(tag).Concat(cipher).ToArray();
    }

    private static byte[] Decrypt(byte[] data, string password)
    {
        if (data.Length < 48 || !data.Take(4).SequenceEqual(Magic)) throw new UserError("Зашифрованная часть копии повреждена");
        var salt = data[4..20];
        var nonce = data[20..32];
        var tag = data[32..48];
        var cipher = data[48..];
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException) { throw new UserError("Неверный пароль копии — ключи и входы не восстановлены"); }
        return plain;
    }

    // ---------- сохранение ----------
    private string Hash()
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var f in Files.Concat(TokenFiles))
        {
            var p = Path.Combine(Config.Profile, f);
            if (File.Exists(p)) sb.Append(f).Append(File.GetLastWriteTimeUtc(p).Ticks).Append(new FileInfo(p).Length);
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public object Save(bool manual)
    {
        var target = FilePath();
        if (target == "") throw new UserError("Выберите папку облака", "pro.cloud.folder");
        if (!Directory.Exists(Cfg.Str("folder").Trim()))
            throw new UserError("Папка облака не найдена — проверьте путь или выберите другую", "pro.cloud.folder");
        var withSecrets = Cfg.Bool("include_secrets");
        var password = Cfg.Str("password");
        if (withSecrets && password.Length < 6)
            throw new UserError("Для копии с ключами и входами задайте пароль не короче 6 символов", "pro.cloud.password");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        var tmp = target + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var media = new HashSet<string>();
            foreach (var f in Files)
            {
                var p = Path.Combine(Config.Profile, f);
                if (!File.Exists(p)) continue;
                var text = File.ReadAllText(p);
                if (f == "settings.json")
                {
                    var data = JsonNode.Parse(text);
                    foreach (var sp in SecretPaths) if (data.At(sp) != null) data.Set(sp, "");
                    data.Set("pro.cloud.password", "");
                    text = data.ToJsonString(J.Pretty);
                }
                if (f == "alerts.json")
                    foreach (var a in JsonNode.Parse(text)?["items"] as JsonArray ?? new JsonArray())
                        foreach (var m in new[] { a.Str("media"), a.Str("sound") })
                            if (m != "") media.Add(m);
                using var w = new StreamWriter(zip.CreateEntry(f).Open());
                w.Write(text);
            }
            foreach (var m in media)
            {
                var p = Path.Combine(Config.MediaDir, m);
                if (File.Exists(p)) zip.CreateEntryFromFile(p, "media/" + m);
            }
            if (withSecrets)
            {
                var secrets = new JsonObject();
                var settings = new JsonObject();
                foreach (var sp in SecretPaths) settings[sp] = _hub.S.Str(sp);
                settings["donationalerts.expires_at"] = _hub.S.Num("donationalerts.expires_at");
                secrets["settings"] = settings;
                foreach (var f in TokenFiles)
                {
                    var p = Path.Combine(Config.Profile, f);
                    if (File.Exists(p)) secrets[f] = File.ReadAllText(p);
                }
                var enc = Encrypt(Encoding.UTF8.GetBytes(secrets.ToJsonString()), password);
                using var s = zip.CreateEntry("secrets.bin").Open();
                s.Write(enc, 0, enc.Length);
            }
            var manifest = new JsonObject
            {
                ["app"] = Config.AppName, ["version"] = Config.Version, ["profile"] = ProfileTitle,
                ["machine"] = Environment.MachineName, ["ts"] = ActionLog.Now, ["secrets"] = withSecrets,
            };
            using var mw = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            mw.Write(manifest.ToJsonString(J.Pretty));
        }
        File.Move(tmp, target, true);
        _lastHash = Hash();
        _hub.State.Data["cloud_saved"] = ActionLog.Now;
        _hub.State.Save();
        Status = $"сохранено {DateTime.Now:dd.MM HH:mm}";
        if (manual) _hub.Log.Add("system", $"Облачная копия сохранена: {target}");
        return new { path = target, secrets = withSecrets };
    }

    // ---------- восстановление ----------
    public object Peek()
    {
        var path = FilePath();
        if (path == "" || !File.Exists(path)) return new { exists = false, path };
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var m = zip.GetEntry("manifest.json");
            using var r = new StreamReader(m.Open());
            var j = JsonNode.Parse(r.ReadToEnd());
            var ts = j.Num("ts");
            var mine = j.Str("machine") == Environment.MachineName;
            var localSaved = _hub.State.Data.Num("cloud_saved");
            var restored = _hub.State.Data.Num("cloud_restored");
            return new
            {
                exists = true, path, ts, machine = j.Str("machine"), secrets = j.Bool("secrets"), version = j.Str("version"),
                // предлагаем восстановить, если копия сделана на другом ПК и она новее того, что здесь уже было
                offer = !mine && ts > Math.Max(localSaved, restored) + 5,
            };
        }
        catch (Exception e) { return new { exists = true, path, error = e.Message }; }
    }

    public object Restore(string password)
    {
        var path = FilePath();
        if (path == "" || !File.Exists(path)) throw new UserError("Копия в папке облака не найдена");
        Backup.Create("before-cloud");
        var keepCloud = _hub.S["pro"]?["cloud"]?.DeepClone();
        var secretsRestored = false;
        double ts = 0;
        using (var zip = ZipFile.OpenRead(path))
        {
            JsonObject secrets = null;
            var sb = zip.GetEntry("secrets.bin");
            if (sb != null && (password ?? "") != "")
            {
                using var ms = new MemoryStream();
                using (var s = sb.Open()) s.CopyTo(ms);
                secrets = JsonNode.Parse(Encoding.UTF8.GetString(Decrypt(ms.ToArray(), password))) as JsonObject;
            }
            foreach (var e in zip.Entries)
            {
                if (Files.Contains(e.FullName))
                {
                    using var r = new StreamReader(e.Open());
                    var text = r.ReadToEnd();
                    if (e.FullName == "settings.json")
                    {
                        var data = JsonNode.Parse(text);
                        // ключи: из зашифрованной части, иначе — текущие с этого компьютера
                        foreach (var sp in SecretPaths)
                            data.Set(sp, secrets != null ? secrets["settings"]?[sp]?.DeepClone() : _hub.S.Str(sp));
                        if (secrets != null) data.Set("donationalerts.expires_at", secrets["settings"]?["donationalerts.expires_at"]?.DeepClone());
                        data.Set("pro.cloud", keepCloud);   // настройки облака на этом ПК не меняем
                        text = data.ToJsonString(J.Pretty);
                    }
                    File.WriteAllText(Path.Combine(Config.Profile, e.FullName), text);
                }
                else if (e.FullName.StartsWith("media/") && e.Name != "" && !e.FullName[6..].Contains('/'))
                    e.ExtractToFile(Path.Combine(Config.MediaDir, e.Name), true);
                else if (e.FullName == "manifest.json")
                {
                    using var r = new StreamReader(e.Open());
                    ts = JsonNode.Parse(r.ReadToEnd()).Num("ts");
                }
            }
            if (secrets != null)
            {
                foreach (var f in TokenFiles)
                    if (secrets[f] is JsonValue v) File.WriteAllText(Path.Combine(Config.Profile, f), v.GetValue<string>());
                secretsRestored = true;
            }
        }
        _hub.State.Data["cloud_restored"] = Math.Max(ts, ActionLog.Now);
        _hub.State.Save();
        _hub.Log.Add("system", "Профиль восстановлен из облачной копии" + (secretsRestored ? " вместе с ключами и входами" : ""));
        return new { secrets = secretsRestored, restart_required = true };
    }

    /// <summary>Автосохранение: раз в 5 минут, если что-то изменилось.</summary>
    public async Task Run()
    {
        _lastHash = Hash();
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            if (!Cfg.Bool("enabled") || !Cfg.Bool("auto", true) || FilePath() == "") continue;
            var h = Hash();
            if (h == _lastHash) continue;
            try { Save(false); }
            catch (Exception e) { Status = "ошибка: " + e.Message; }
        }
    }

    /// <summary>Сохранение при выходе из программы (если включено автосохранение).</summary>
    public void SaveOnExit()
    {
        try
        {
            if (Cfg.Bool("enabled") && Cfg.Bool("auto", true) && FilePath() != "" && Hash() != _lastHash) Save(false);
        }
        catch { }
    }
}
