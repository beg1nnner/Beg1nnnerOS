using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Перенос своих команд из Nightbot, StreamElements и moo.bot (их публичные списки команд канала)
/// или из текста «!команда ответ». Переменные чужих ботов переводятся в подстановки Beg1nnnerOS.
/// </summary>
public sealed class CommandImport
{
    private readonly Hub _hub;
    public CommandImport(Hub hub) => _hub = hub;

    private static readonly Regex NameRe = new(@"^![\w-]{1,30}$");

    private sealed class Item
    {
        public string Name = "", Response = "", Original = "", Access = "everyone";
        public List<string> Aliases = new();
        public int Cooldown, RandomMax = 100;
        public List<string> Warnings = new();
    }

    // ---------- загрузка ----------
    public async Task<object> Fetch(string source, string login)
    {
        login = (login ?? "").Trim().TrimStart('@').ToLowerInvariant();
        var m = Regex.Match(login, @"(?:twitch\.tv|nightbot\.tv/t|streamelements\.com|moo\.bot/r)/([a-z0-9_]{3,25})");
        if (m.Success) login = m.Groups[1].Value;
        if (!Regex.IsMatch(login, "^[a-z0-9_]{3,25}$")) throw new UserError("Укажите ник канала на Twitch (как в адресе twitch.tv/ник)");
        List<Item> items;
        try
        {
            items = source switch
            {
                "nightbot" => await Nightbot(login),
                "streamelements" => await StreamElements(login),
                "moobot" => await Moobot(login),
                _ => throw new UserError("Неизвестный источник"),
            };
        }
        catch (HttpRequestException e) { throw new UserError($"{Title(source)} не отвечает ({e.Message}). Проверьте интернет или попробуйте позже"); }
        catch (TaskCanceledException) { throw new UserError($"{Title(source)} не ответил вовремя — попробуйте ещё раз"); }
        catch (System.Text.Json.JsonException) { throw new UserError($"{Title(source)} прислал непонятный ответ — возможно, у сервиса изменился формат"); }
        return Result(items, source);
    }

    public object FromText(string text)
    {
        var items = new List<Item>();
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line == "") continue;
            // «!cmd ответ», «!cmd — ответ», «!cmd: ответ», «!cmd<TAB>ответ», «!commands add !cmd ответ»
            line = Regex.Replace(line, @"^!(?:commands?|cmd|addcom|addcommand)\s+(?:add\s+)?", "", RegexOptions.IgnoreCase);
            var mm = Regex.Match(line, @"^(!?[\w-]+)\s*(?:[—–:\-]\s*|\t|\s+)(.+)$");
            if (!mm.Success) continue;
            var name = mm.Groups[1].Value;
            if (!name.StartsWith("!")) name = "!" + name;
            items.Add(Convert(name, mm.Groups[2].Value.Trim(), "auto"));
        }
        if (items.Count == 0) throw new UserError("Не нашёл ни одной команды. Формат: по одной в строке — «!команда ответ»");
        return Result(items, "text");
    }

    private async Task<JsonNode> Get(string url, Dictionary<string, string> headers = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers != null) foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        using var resp = await _hub.Http.SendAsync(req);
        if ((int)resp.StatusCode == 404) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"ошибка {(int)resp.StatusCode}");
        var text = await resp.Content.ReadAsStringAsync();
        return JsonNode.Parse(text);
    }

    private async Task<List<Item>> Nightbot(string login)
    {
        var ch = await Get($"https://api.nightbot.tv/1/channels/t/{login}");
        var id = ch?.Str("channel._id") ?? "";
        if (id == "") throw new UserError($"Nightbot не знает канал «{login}» — проверьте ник или подключён ли там Nightbot");
        var js = await Get("https://api.nightbot.tv/1/commands", new() { ["Nightbot-Channel"] = id });
        var list = new List<Item>();
        foreach (var c in js?["commands"] as JsonArray ?? new JsonArray())
        {
            var name = c.Str("name");
            if (!name.StartsWith("!") || name.Length < 2) continue;   // без «!» Nightbot реагирует на слово в любом месте — у нас так не бывает
            var it = Convert(name, c.Str("message"), "nightbot");
            it.Access = c.Str("userLevel") switch
            {
                "owner" => "broadcaster", "moderator" => "mod", "twitch_vip" or "regular" => "vip", "subscriber" => "sub", _ => "everyone",
            };
            it.Cooldown = Math.Clamp(c.Int("coolDown"), 0, 86400);
            list.Add(it);
        }
        return list;
    }

    private async Task<List<Item>> StreamElements(string login)
    {
        var ch = await Get($"https://api.streamelements.com/kappa/v2/channels/{login}");
        var id = ch?.Str("_id") ?? "";
        if (id == "") throw new UserError($"StreamElements не знает канал «{login}» — проверьте ник");
        var js = await Get($"https://api.streamelements.com/kappa/v2/bot/commands/{id}/public");
        var list = new List<Item>();
        foreach (var c in js as JsonArray ?? new JsonArray())
        {
            if (!c.Bool("enabled", true)) continue;
            var it = Convert("!" + c.Str("command").TrimStart('!'), c.Str("reply"), "streamelements");
            it.Aliases = c.StrList("aliases").Select(a => "!" + a.TrimStart('!')).ToList();
            var lvl = c.Int("accessLevel", 100);
            it.Access = lvl <= 100 ? "everyone" : lvl <= 300 ? "sub" : lvl < 500 ? "vip" : lvl < 1500 ? "mod" : "broadcaster";
            it.Cooldown = Math.Clamp(c.Int("cooldown.global"), 0, 86400);
            list.Add(it);
        }
        return list;
    }

    private async Task<List<Item>> Moobot(string login)
    {
        var meta = await Get($"https://api.moo.bot/1/channel/meta?name={login}");
        var uid = meta?.Str("channel.userid") ?? "";
        if (uid == "") throw new UserError($"moo.bot не знает канал «{login}» — проверьте ник или подключён ли там Moobot");
        var js = await Get($"https://api.moo.bot/1/channel/public/commands/list?channel={uid}");
        var all = js?["list"] as JsonArray ?? new JsonArray();
        var aliases = all.Where(c => c.Str("type") == "alias" && c.Str("command") != "")
            .GroupBy(c => c.Str("command")).ToDictionary(g => g.Key, g => g.Select(a => "!" + a.Str("identifier")).ToList());
        var list = new List<Item>();
        foreach (var c in all)
        {
            var ident = c.Str("identifier");
            if (c.Str("type") != "custom" || ident == "" || ident.Contains(' ')) continue;
            var it = Convert("!" + ident, c.Str("response"), "moobot");
            if (aliases.TryGetValue(ident, out var al)) it.Aliases = al;
            list.Add(it);   // moo.bot показывает публично только команды для всех
        }
        return list;
    }

    // ---------- перевод переменных ----------
    private static readonly (Regex Re, string To)[] NightbotVars =
    {
        (new(@"\$\((?:twitch\s+\$\(channel\)\s+""\{\{uptimeLength\}\}""|uptime)\)", RegexOptions.IgnoreCase), "{uptime}"),
        (new(@"\$\((?:twitch\s+\$\(channel\)\s+""\{\{game\}\}""|game)\)", RegexOptions.IgnoreCase), "{game}"),
        (new(@"\$\((?:twitch\s+\$\(channel\)\s+""\{\{title\}\}""|title)\)", RegexOptions.IgnoreCase), "{title}"),
        (new(@"\$\((?:user|user\.name|sender)\)", RegexOptions.IgnoreCase), "{user}"),
        (new(@"\$\(touser\)", RegexOptions.IgnoreCase), "{touser}"),
        (new(@"\$\((?:query|querystring)\)", RegexOptions.IgnoreCase), "{args}"),
        (new(@"\$\(([1-9])\)"), "{$1}"),
        (new(@"\$\(channel\)", RegexOptions.IgnoreCase), "{channel}"),
        (new(@"\$\(count\)", RegexOptions.IgnoreCase), "{count}"),
    };
    private static readonly (Regex Re, string To)[] SeVars =
    {
        (new(@"\$\{(?:user|user\.name|sender|source|user\.display_name)\}", RegexOptions.IgnoreCase), "{user}"),
        (new(@"\$\{(?:touser|touser\.name)\}", RegexOptions.IgnoreCase), "{touser}"),
        (new(@"\$\{(?:1:|0:|args|query)\}", RegexOptions.IgnoreCase), "{args}"),
        (new(@"\$\{([1-9])\}"), "{$1}"),
        (new(@"\$\{(?:channel|channel\.name|channel\.display_name)\}", RegexOptions.IgnoreCase), "{channel}"),
        (new(@"\$\{(?:count|getcount\s+[^}]*)\}", RegexOptions.IgnoreCase), "{count}"),
        (new(@"\$\{uptime\}", RegexOptions.IgnoreCase), "{uptime}"),
        (new(@"\$\{game\}", RegexOptions.IgnoreCase), "{game}"),
        (new(@"\$\{title\}", RegexOptions.IgnoreCase), "{title}"),
    };
    private static readonly Regex SeRandom = new(@"\$\{random\.(\d+)-(\d+)\}", RegexOptions.IgnoreCase);
    private static readonly Regex NbRandom = new(@"\$\(eval\s+Math\.floor\(\s*\(?\s*Math\.random\(\)\s*\*\s*(\d+)\s*\)?\s*\+\s*1\s*\)\s*\)", RegexOptions.IgnoreCase);
    private static readonly Regex Leftover = new(@"\$\([^)]*\)|\$\{[^}]*\}", RegexOptions.IgnoreCase);

    private static Item Convert(string name, string response, string source)
    {
        var it = new Item { Name = name, Original = response ?? "" };
        var r = response ?? "";
        var src = source;
        if (src == "auto") src = r.Contains("${") ? "streamelements" : r.Contains("$(") ? "nightbot" : "";
        // случайное число: у нас {random} от 1 до N
        r = SeRandom.Replace(r, m =>
        {
            if (m.Groups[1].Value != "1") it.Warnings.Add($"случайное число теперь от 1 до {m.Groups[2].Value}");
            it.RandomMax = Math.Clamp(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), 1, 1000);
            return "{random}";
        });
        r = NbRandom.Replace(r, m => { it.RandomMax = Math.Clamp(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 1, 1000); return "{random}"; });
        foreach (var (re, to) in src == "streamelements" ? SeVars : src == "nightbot" ? NightbotVars : SeVars.Concat(NightbotVars).ToArray())
            r = re.Replace(r, to);
        var left = Leftover.Matches(r).Select(m => m.Value).Distinct().Take(3).ToList();
        if (left.Count > 0)
            it.Warnings.Add("не переносится: " + string.Join(", ", left.Select(x => x.Length > 40 ? x[..40] + "…" : x))
                + (left.Any(x => Regex.IsMatch(x, "urlfetch|customapi|eval|readapi", RegexOptions.IgnoreCase)) ? " (запросы к сайтам и скрипты)" : ""));
        it.Response = r.Trim();
        return it;
    }

    // ---------- результат для панели ----------
    private object Result(List<Item> items, string source)
    {
        var taken = _hub.Commands.AllNames();
        var seen = new HashSet<string>();
        var rows = new List<object>();
        foreach (var it in items)
        {
            var status = "new";
            var reason = "";
            var low = it.Name.ToLowerInvariant();
            if (!NameRe.IsMatch(it.Name)) { status = "invalid"; reason = "имя не подходит (только буквы, цифры, _ и -, до 30 символов)"; }
            else if (it.Response == "") { status = "invalid"; reason = "пустой ответ"; }
            else if (taken.TryGetValue(low, out var f)) { status = "exists"; reason = f.Kind == "standard" ? "такая команда уже есть среди стандартных" : "такая команда уже есть"; }
            else if (!seen.Add(low)) { status = "invalid"; reason = "повторяется в списке"; }
            var aliases = it.Aliases.Where(a => NameRe.IsMatch(a) && !taken.ContainsKey(a.ToLowerInvariant()) && a.ToLowerInvariant() != low)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (aliases.Count < it.Aliases.Count) it.Warnings.Add("часть псевдонимов пропущена (заняты или неверные)");
            if (it.Response.Length > 500) it.Warnings.Add("ответ обрезан до 500 символов");
            rows.Add(new
            {
                name = it.Name, aliases, response = it.Response.Length > 500 ? it.Response[..500] : it.Response,
                original = it.Original, access = it.Access, cooldown = it.Cooldown, random_max = it.RandomMax,
                warnings = it.Warnings, status, reason,
            });
        }
        return new { source, title = Title(source), items = rows };
    }

    /// <summary>Добавляет выбранные команды. Ошибки по отдельным командам не останавливают остальные.</summary>
    public object Import(JsonNode list)
    {
        if (list is not JsonArray arr || arr.Count == 0) throw new UserError("Не выбрано ни одной команды");
        var added = 0;
        var errors = new List<string>();
        foreach (var c in arr.Take(300))
        {
            try
            {
                _hub.Commands.SaveCustom(new JsonObject
                {
                    ["name"] = c.Str("name"), ["aliases"] = c["aliases"]?.DeepClone() ?? new JsonArray(),
                    ["response"] = c.Str("response"), ["access"] = c.Str("access", "everyone"),
                    ["cooldown"] = Math.Clamp(c.Int("cooldown"), 0, 86400), ["random_max"] = Math.Clamp(c.Int("random_max", 100), 1, 1000),
                    ["enabled"] = true,
                });
                added++;
            }
            catch (UserError e) { errors.Add($"{c.Str("name")}: {e.Message}"); }
        }
        _hub.Log.Add("system", $"Импорт команд: добавлено {added}" + (errors.Count > 0 ? $", пропущено {errors.Count}" : ""));
        return new { added, errors };
    }

    private static string Title(string source) => source switch
    {
        "nightbot" => "Nightbot", "streamelements" => "StreamElements", "moobot" => "moo.bot", _ => "Текст",
    };
}
