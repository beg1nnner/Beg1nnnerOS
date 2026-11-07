using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Beg1nnnerOS.Core;

/// <summary>
/// Профили: у каждого своя папка с настройками, входами Twitch, командами и статистикой.
/// Основной профиль лежит прямо в корневой папке программы — так сохраняется совместимость со старыми версиями.
/// </summary>
public static class Profiles
{
    public const string MainTitle = "Основной";
    private static readonly char[] Bad = Path.GetInvalidFileNameChars();

    private static string IndexPath(string root) => Path.Combine(root, "profiles.json");

    private static JsonObject ReadIndex(string root)
    {
        try
        {
            if (File.Exists(IndexPath(root)) && JsonNode.Parse(File.ReadAllText(IndexPath(root))) is JsonObject o) return o;
        }
        catch { }
        return new JsonObject { ["active"] = "" };
    }

    private static void WriteIndex(string root, JsonObject o)
    {
        var tmp = IndexPath(root) + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(J.Pretty));
        File.Move(tmp, IndexPath(root), true);
    }

    /// <summary>Безопасное имя папки профиля (пустая строка — основной).</summary>
    public static string Clean(string name)
    {
        name = (name ?? "").Trim();
        if (name == "" || name == MainTitle) return "";
        var chars = name.Select(c => Bad.Contains(c) || c == '.' ? '_' : c).ToArray();
        var clean = new string(chars).Trim(' ', '_');
        return clean.Length > 40 ? clean[..40] : clean;
    }

    public static string PathFor(string root, string name)
    {
        name = Clean(name);
        return name == "" ? root : Path.Combine(root, "profiles", name);
    }

    /// <summary>Активный профиль; если его папку удалили вручную — возвращаемся к основному.</summary>
    public static string ActiveName(string root)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HUB_PROFILE"))) return "";
        var name = Clean(ReadIndex(root).Str("active"));
        return name != "" && Directory.Exists(PathFor(root, name)) ? name : "";
    }

    public static List<object> List(string root, string active)
    {
        var list = new List<object> { new { name = MainTitle, id = "", active = active == "" } };
        var dir = Path.Combine(root, "profiles");
        if (Directory.Exists(dir))
            foreach (var d in new DirectoryInfo(dir).GetDirectories().OrderBy(d => d.Name))
                list.Add(new { name = d.Name, id = d.Name, active = d.Name == active });
        return list;
    }

    /// <summary>Создаёт профиль. copyFrom — папка, из которой взять настройки (без входов и статистики).</summary>
    public static string Create(string root, string name, string copyFrom)
    {
        var id = Clean(name);
        if (id == "") throw new Twitch.UserError("Введите название профиля, например название канала");
        var path = PathFor(root, id);
        if (Directory.Exists(path)) throw new Twitch.UserError($"Профиль «{id}» уже есть");
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "media"));
        Directory.CreateDirectory(Path.Combine(path, "backups"));
        if (!string.IsNullOrEmpty(copyFrom))
        {
            foreach (var f in new[] { "commands.json", "timers.json", "alerts.json" })
            {
                var src = Path.Combine(copyFrom, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(path, f));
            }
            var settings = Path.Combine(copyFrom, "settings.json");
            if (File.Exists(settings))
            {
                // Ключи приложения Twitch можно оставить, а входы и токены сервисов — нет: это другой канал
                var data = JsonNode.Parse(File.ReadAllText(settings));
                data.Set("donationalerts.token", "");
                data.Set("donationalerts.refresh_token", "");
                data.Set("donationalerts.expires_at", 0);
                data.Set("twitch.bot_login", "");
                File.WriteAllText(Path.Combine(path, "settings.json"), data.ToJsonString(J.Pretty));
            }
            var media = Path.Combine(copyFrom, "media");
            if (Directory.Exists(media))
                foreach (var f in Directory.GetFiles(media))
                    File.Copy(f, Path.Combine(path, "media", Path.GetFileName(f)), true);
        }
        return id;
    }

    public static void SetActive(string root, string name)
    {
        var id = Clean(name);
        if (id != "" && !Directory.Exists(PathFor(root, id))) throw new Twitch.UserError("Такого профиля нет");
        var o = ReadIndex(root);
        o["active"] = id;
        WriteIndex(root, o);
    }

    public static void Delete(string root, string name, string active)
    {
        var id = Clean(name);
        if (id == "") throw new Twitch.UserError("Основной профиль удалить нельзя");
        if (id == active) throw new Twitch.UserError("Сначала переключитесь на другой профиль");
        var path = PathFor(root, id);
        if (!Directory.Exists(path)) throw new Twitch.UserError("Такого профиля нет");
        Directory.Delete(path, true);
    }
}
