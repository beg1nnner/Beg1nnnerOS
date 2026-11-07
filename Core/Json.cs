using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beg1nnnerOS.Core;

/// <summary>Помощники для работы с JsonNode — настройки хранятся в том же формате, что и в Python-версии.</summary>
public static class J
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static JsonNode Parse(string text) => JsonNode.Parse(text);

    /// <summary>
    /// Разбор JSON от сторонних сервисов, которые иногда повторяют ключ в одном объекте
    /// (так бывает у Яндекс Музыки: два поля "albums"). Обычный JsonNode.Parse на этом падает
    /// с ошибкой «An item with the same key has already been added»; здесь побеждает последнее значение.
    /// </summary>
    public static JsonNode ParseLoose(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return FromElement(doc.RootElement);
    }

    private static JsonNode FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = FromElement(p.Value);   // повтор ключа перезаписывает
                return o;
            case JsonValueKind.Array:
                var a = new JsonArray();
                foreach (var x in e.EnumerateArray()) a.Add(FromElement(x));
                return a;
            case JsonValueKind.String: return JsonValue.Create(e.GetString());
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var l)) return JsonValue.Create(l);
                return JsonValue.Create(e.GetDouble());
            case JsonValueKind.True: return JsonValue.Create(true);
            case JsonValueKind.False: return JsonValue.Create(false);
            default: return null;
        }
    }

    /// <summary>Значение по пути вида "music.rewards.order.title".</summary>
    public static JsonNode At(this JsonNode node, string path)
    {
        var cur = node;
        foreach (var key in path.Split('.'))
        {
            if (cur is JsonObject o && o.TryGetPropertyValue(key, out var next))
                cur = next;
            else
                return null;
        }
        return cur;
    }

    public static JsonObject Obj(this JsonNode node, string path)
    {
        return node.At(path) as JsonObject;
    }

    public static JsonArray Arr(this JsonNode node, string path)
    {
        return node.At(path) as JsonArray;
    }

    public static string Str(this JsonNode node, string path, string def = "")
    {
        return AsStr(node?.At(path), def);
    }

    public static double Num(this JsonNode node, string path, double def = 0)
    {
        return AsNum(node?.At(path), def);
    }

    public static int Int(this JsonNode node, string path, int def = 0)
    {
        return (int)Math.Round(AsNum(node?.At(path), def));
    }

    public static bool Bool(this JsonNode node, string path, bool def = false)
    {
        return AsBool(node?.At(path), def);
    }

    public static string AsStr(JsonNode n, string def = "")
    {
        if (n is JsonValue v)
        {
            var kind = v.GetValueKind();
            if (kind == JsonValueKind.String) return v.GetValue<string>();
            if (kind == JsonValueKind.Number) return v.ToJsonString();
            if (kind == JsonValueKind.True) return "true";
            if (kind == JsonValueKind.False) return "false";
        }
        return def;
    }

    public static double AsNum(JsonNode n, double def = 0)
    {
        if (n is JsonValue v)
        {
            var kind = v.GetValueKind();
            if (kind == JsonValueKind.Number)
                return double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
            if (kind == JsonValueKind.String &&
                double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            if (kind == JsonValueKind.True) return 1;
            if (kind == JsonValueKind.False) return 0;
        }
        return def;
    }

    public static int AsInt(JsonNode n, int def = 0) => (int)Math.Round(AsNum(n, def));

    public static bool AsBool(JsonNode n, bool def = false)
    {
        if (n is JsonValue v)
        {
            var kind = v.GetValueKind();
            if (kind == JsonValueKind.True) return true;
            if (kind == JsonValueKind.False) return false;
            if (kind == JsonValueKind.Number) return AsNum(n) != 0;
            if (kind == JsonValueKind.String) return v.GetValue<string>() is "true" or "1" or "on";
        }
        return def;
    }

    public static List<string> StrList(this JsonNode node, string path)
    {
        var list = new List<string>();
        if (node?.At(path) is JsonArray a)
            foreach (var item in a)
            {
                var s = AsStr(item);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        return list;
    }

    /// <summary>Установка значения по пути с созданием промежуточных объектов.</summary>
    public static void Set(this JsonNode node, string path, JsonNode value)
    {
        var keys = path.Split('.');
        var cur = (JsonObject)node;
        for (var i = 0; i < keys.Length - 1; i++)
        {
            if (cur[keys[i]] is not JsonObject next)
            {
                next = new JsonObject();
                cur[keys[i]] = next;
            }
            cur = next;
        }
        cur[keys[^1]] = value?.Parent != null ? value.DeepClone() : value;
    }

    public static JsonNode Clone(this JsonNode n) => n?.DeepClone();

    /// <summary>Дополняет данные недостающими ключами из значений по умолчанию (как deep_merge в Python).</summary>
    public static JsonNode DeepMerge(JsonNode defaults, JsonNode data)
    {
        if (defaults is JsonObject d && data is JsonObject o)
        {
            var result = new JsonObject();
            foreach (var kv in d)
                result[kv.Key] = o.TryGetPropertyValue(kv.Key, out var v) ? DeepMerge(kv.Value, v) : kv.Value?.DeepClone();
            foreach (var kv in o)
                if (!d.ContainsKey(kv.Key))
                    result[kv.Key] = kv.Value?.DeepClone();
            return result;
        }
        if (data == null) return defaults?.DeepClone();
        return data.DeepClone();
    }

    public static JsonNode From(object value)
    {
        if (value == null) return null;
        if (value is JsonNode n) return n.Parent != null ? n.DeepClone() : n;
        return JsonSerializer.SerializeToNode(value, Options);
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>JSON-файл с атомарной записью и значениями по умолчанию.</summary>
public sealed class JsonStore
{
    public string Path { get; }
    private readonly JsonNode _defaults;
    public JsonObject Data { get; set; }

    public JsonStore(string path, JsonNode defaults = null)
    {
        Path = path;
        _defaults = defaults ?? new JsonObject();
        Data = Load();
    }

    private JsonObject Load()
    {
        JsonNode raw = null;
        try
        {
            if (File.Exists(Path))
                raw = JsonNode.Parse(File.ReadAllText(Path));
        }
        catch (Exception)
        {
            try { File.Move(Path, Path + ".broken-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()); } catch { }
            raw = null;
        }
        return (JsonObject)J.DeepMerge(_defaults, raw);
    }

    public void Reload() => Data = Load();

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Data.ToJsonString(J.Pretty));
        File.Move(tmp, Path, true);
    }
}
