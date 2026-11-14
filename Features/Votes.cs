using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Features;

/// <summary>
/// Опросы и прогнозы Twitch прямо из программы: запуск, живые результаты, завершение,
/// выбор победителя, итоги в чат и источник для OBS (/votes). Настройки — раздел «votes».
/// </summary>
public sealed class Votes
{
    public const string PollScope = "channel:manage:polls";
    public const string PredScope = "channel:manage:predictions";

    private readonly Hub _hub;
    private JsonObject _poll, _pred;            // последние данные от Twitch (сырые)
    private string _pollError = "", _predError = "";
    private double _fetchedAt, _viewedAt;
    private string _lastPollId = "", _lastPredId = "";
    private string _pollStatus = "", _predStatus = "";

    public Votes(Hub hub) => _hub = hub;
    private JsonObject Cfg => _hub.S.Obj("votes") ?? new JsonObject();
    private TwitchClient Tw => _hub.Twitch;

    public bool HasPollScope => Tw.Streamer.Scopes.Contains(PollScope);
    public bool HasPredScope => Tw.Streamer.Scopes.Contains(PredScope);

    // ---------- фоновое обновление ----------
    /// <summary>Пока идёт опрос/прогноз или открыта панель/оверлей — опрашиваем Twitch раз в 3 с, иначе раз в минуту.</summary>
    public async Task Run()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        while (true)
        {
            var now = ActionLog.Now;
            var busy = IsActive(_poll) || IsActive(_pred) || now - _viewedAt < 30;
            if (Tw.Streamer.Ready && (busy || now - _fetchedAt > 60))
            {
                try { await Refresh(); } catch (Exception e) { _hub.Log.Add("error", "Опросы/прогнозы: " + e.Message); }
            }
            await Task.Delay(TimeSpan.FromSeconds(busy ? 3 : 10));
        }
    }

    private static bool IsActive(JsonObject o) => o != null && o.Str("status") is "ACTIVE" or "LOCKED";

    public async Task Refresh()
    {
        if (!Tw.Streamer.Ready) return;
        _fetchedAt = ActionLog.Now;
        if (HasPollScope)
        {
            try
            {
                var p = TwitchClient.First(await Tw.Api(HttpMethod.Get, "/polls",
                    new() { ["broadcaster_id"] = Tw.BroadcasterId, ["first"] = "1" })) as JsonObject;
                _pollError = "";
                OnPoll(p);
            }
            catch (TwitchException e) { _pollError = Explain(e, "Опросы"); }
        }
        if (HasPredScope)
        {
            try
            {
                var p = TwitchClient.First(await Tw.Api(HttpMethod.Get, "/predictions",
                    new() { ["broadcaster_id"] = Tw.BroadcasterId, ["first"] = "1" })) as JsonObject;
                _predError = "";
                OnPred(p);
            }
            catch (TwitchException e) { _predError = Explain(e, "Прогнозы"); }
        }
    }

    private void OnPoll(JsonObject p)
    {
        var id = p?.Str("id") ?? "";
        var status = p?.Str("status") ?? "";
        // итог в чат — только если мы видели этот опрос активным (не публикуем старые опросы после перезапуска)
        if (id != "" && id == _lastPollId && _pollStatus == "ACTIVE" && status is "COMPLETED" or "TERMINATED")
            ChatResult(PollResultText(p), "poll");
        _poll = p; _lastPollId = id; _pollStatus = status;
    }

    private void OnPred(JsonObject p)
    {
        var id = p?.Str("id") ?? "";
        var status = p?.Str("status") ?? "";
        if (id != "" && id == _lastPredId && _predStatus is "ACTIVE" or "LOCKED" && status != _predStatus)
        {
            if (status == "RESOLVED") ChatResult(PredResultText(p), "prediction");
            else if (status == "CANCELED") ChatResult($"🔮 Прогноз «{p.Str("title")}» отменён — баллы возвращены", "prediction");
            else if (status == "LOCKED" && Cfg.Bool("chat_lock", true)) ChatResult($"🔒 Ставки на «{p.Str("title")}» закрыты! Ждём результата", "prediction");
        }
        _pred = p; _lastPredId = id; _predStatus = status;
    }

    private void ChatResult(string text, string kind)
    {
        if (!Cfg.Bool("chat_results", true) || string.IsNullOrEmpty(text)) return;
        // о завершении опроса уже пишет уведомление «Опрос» из раздела «События» — не дублируем
        if (kind == "poll" && _hub.S.Bool("events.templates.poll.enabled")) return;
        _hub.Log.Add("system", text);
        _hub.Loop.Start(async () => { try { await Tw.SendChat(text); } catch (Exception e) { _hub.Log.Add("error", "Итог в чат: " + e.Message); } });
    }

    public string PollResultText(JsonObject p)
    {
        var ch = Choices(p);
        var total = ch.Sum(c => c.Votes);
        if (total == 0) return $"📊 Опрос «{p.Str("title")}» завершён — никто не проголосовал";
        var parts = ch.OrderByDescending(c => c.Votes).Select(c => $"{c.Title} — {Pct(c.Votes, total)}%");
        var max = ch.Max(c => c.Votes);
        var winners = ch.Where(c => c.Votes == max).ToList();
        var head = winners.Count > 1 ? "ничья!" : $"победил «{winners[0].Title}»";
        return $"📊 Опрос «{p.Str("title")}» завершён: {head} ({total} {Plural(total, "голос", "голоса", "голосов")}). " + string.Join(" · ", parts);
    }

    public string PredResultText(JsonObject p)
    {
        var win = p.Str("winning_outcome_id");
        var outs = Outcomes(p);
        var w = outs.FirstOrDefault(o => o.Id == win);
        if (w == null) return $"🔮 Прогноз «{p.Str("title")}» завершён";
        var total = outs.Sum(o => o.Points);
        var top = (p.At("outcomes") as JsonArray)?.FirstOrDefault(o => o.Str("id") == win)?["top_predictors"] as JsonArray;
        var best = top?.OrderByDescending(t => t.Num("channel_points_won")).FirstOrDefault();
        var tail = best != null && best.Num("channel_points_won") > 0
            ? $" Больше всех выиграл(а) {best.Str("user_name")}: +{Fmt(best.Num("channel_points_won"))} баллов!" : "";
        return $"🔮 Прогноз «{p.Str("title")}»: победил исход «{w.Title}»! {w.Users} {Plural(w.Users, "зритель", "зрителя", "зрителей")} делят {Fmt(total)} баллов.{tail}";
    }

    // ---------- состояние для панели, док-панели и оверлея ----------
    public object State(bool view = true)
    {
        if (view) _viewedAt = ActionLog.Now;
        var c = Cfg;
        return new
        {
            ready = Tw.Streamer.Ready,
            scopes = new { polls = HasPollScope, predictions = HasPredScope },
            poll = PollView(_poll),
            prediction = PredView(_pred),
            poll_error = _pollError,
            prediction_error = _predError,
            templates = new { polls = Tpl(c.Arr("poll_templates"), "choices"), predictions = Tpl(c.Arr("pred_templates"), "outcomes") },
            overlay = new { hide_sec = c.Int("overlay_hide_sec", 20), show_points = c.Bool("overlay_points", true) },
            now = ActionLog.Now,
        };
    }

    /// <summary>Шаблоны для панели; готовые русские шаблоны при английском языке показываются по-английски.</summary>
    private static JsonArray Tpl(JsonArray src, string optsKey)
    {
        var arr = (JsonArray)(src?.DeepClone() ?? new JsonArray());
        if (!L.En) return arr;
        foreach (var t in arr.OfType<JsonObject>())
        {
            t["title"] = L.TE(t.Str("title"));
            if (t[optsKey] is JsonArray o) t[optsKey] = new JsonArray(o.Select(x => (JsonNode)L.TE(J.AsStr(x))).ToArray());
        }
        return arr;
    }

    private record Choice(string Id, string Title, int Votes, int Points);
    private record Outcome(string Id, string Title, int Users, double Points, string Color);

    private static List<Choice> Choices(JsonObject p) =>
        (p?["choices"] as JsonArray ?? new JsonArray()).Select(c => new Choice(c.Str("id"), c.Str("title"), c.Int("votes"), c.Int("channel_points_votes"))).ToList();
    private static List<Outcome> Outcomes(JsonObject p) =>
        (p?["outcomes"] as JsonArray ?? new JsonArray()).Select(o => new Outcome(o.Str("id"), o.Str("title"), o.Int("users"), o.Num("channel_points"), o.Str("color"))).ToList();

    private static double Ts(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUnixTimeMilliseconds() / 1000.0 : 0;

    private static object PollView(JsonObject p)
    {
        if (p == null || p.Str("status") is "ARCHIVED" or "INVALID" or "MODERATED") return null;
        var ch = Choices(p);
        var total = ch.Sum(c => c.Votes);
        var started = Ts(p.Str("started_at"));
        var ends = started + p.Int("duration");
        var ended = Ts(p.Str("ended_at"));
        return new
        {
            id = p.Str("id"), title = p.Str("title"), status = p.Str("status"),
            active = p.Str("status") == "ACTIVE",
            started, ends, ended = ended > 0 ? ended : (p.Str("status") == "ACTIVE" ? 0 : ends),
            duration = p.Int("duration"), total,
            points = p.Bool("channel_points_voting_enabled"), points_per_vote = p.Int("channel_points_per_vote"),
            choices = ch.Select(c => new { id = c.Id, title = c.Title, votes = c.Votes, points_votes = c.Points, pct = Pct(c.Votes, total) }),
        };
    }

    private static object PredView(JsonObject p)
    {
        if (p == null) return null;
        var outs = Outcomes(p);
        var total = outs.Sum(o => o.Points);
        var users = outs.Sum(o => o.Users);
        var created = Ts(p.Str("created_at"));
        var raw = p["outcomes"] as JsonArray ?? new JsonArray();
        return new
        {
            id = p.Str("id"), title = p.Str("title"), status = p.Str("status"),
            active = p.Str("status") == "ACTIVE", locked = p.Str("status") == "LOCKED",
            created, locks = created + p.Int("prediction_window"),
            ended = Ts(p.Str("ended_at")), window = p.Int("prediction_window"),
            winner = p.Str("winning_outcome_id"), total_points = total, total_users = users,
            outcomes = outs.Select((o, i) => new
            {
                id = o.Id, title = o.Title, users = o.Users, points = o.Points, color = o.Color.ToLowerInvariant(),
                pct = Pct(o.Points, total),
                // коэффициент выплаты: весь банк / ставки на этот исход
                ratio = o.Points > 0 ? Math.Round(total / o.Points, 2) : 0,
                top = (raw.Count > i ? raw[i]?["top_predictors"] as JsonArray : null)?
                    .OrderByDescending(t => t.Num("channel_points_used")).Take(3)
                    .Select(t => new { name = t.Str("user_name"), points = t.Num("channel_points_used") }) ?? Enumerable.Empty<object>(),
            }),
        };
    }

    // ---------- действия ----------
    public async Task<string> StartPoll(JsonNode b)
    {
        NeedScope(PollScope, "опросы");
        var title = (b.Str("title")).Trim();
        if (title == "") throw new UserError("Напишите вопрос опроса");
        if (title.Length > 60) throw new UserError($"Вопрос длиннее 60 символов ({title.Length}) — сократите");
        var choices = Clean(b.StrList("choices"));
        if (choices.Count < 2) throw new UserError("Нужно минимум 2 варианта ответа");
        if (choices.Count > 5) throw new UserError("В опросе Twitch не больше 5 вариантов");
        var longC = choices.FirstOrDefault(c => c.Length > 25);
        if (longC != null) throw new UserError($"Вариант «{longC}» длиннее 25 символов — сократите");
        var dur = Math.Clamp(b.Int("duration", 120), 15, 1800);
        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Tw.BroadcasterId, ["title"] = title,
            ["choices"] = choices.Select(c => new { title = c }).ToArray(), ["duration"] = dur,
        };
        var pts = b.Int("points_per_vote");
        if (b.Bool("points") && pts > 0)
        {
            body["channel_points_voting_enabled"] = true;
            body["channel_points_per_vote"] = Math.Clamp(pts, 1, 1000000);
        }
        var p = await Call(() => Tw.Api(HttpMethod.Post, "/polls", null, body), "Опрос");
        OnPoll(TwitchClient.First(p) as JsonObject);
        _viewedAt = ActionLog.Now;
        _hub.Log.Add("system", $"Запущен опрос «{title}» на {FmtDur(dur)}: {string.Join(" / ", choices)}");
        if (Cfg.Bool("chat_start"))
            _hub.Loop.Start(async () => { try { await Tw.SendChat($"📊 Опрос: {title} — голосуйте! Варианты: {string.Join(" / ", choices)}"); } catch { } });
        return "Опрос запущен";
    }

    public async Task<string> EndPoll(bool archive)
    {
        NeedScope(PollScope, "опросы");
        var id = _poll?.Str("id") ?? "";
        if (id == "") throw new UserError("Нет опроса");
        var status = archive ? "ARCHIVED" : "TERMINATED";
        if (!archive && _poll.Str("status") != "ACTIVE") throw new UserError("Опрос уже завершён");
        var p = await Call(() => Tw.Api(HttpMethod.Patch, "/polls", null,
            new { broadcaster_id = Tw.BroadcasterId, id, status }), "Опрос");
        if (archive) { _poll = null; return "Опрос скрыт"; }
        OnPoll(TwitchClient.First(p) as JsonObject);
        return "Опрос завершён";
    }

    public async Task<string> StartPrediction(JsonNode b)
    {
        NeedScope(PredScope, "прогнозы");
        var title = b.Str("title").Trim();
        if (title == "") throw new UserError("Напишите вопрос прогноза");
        if (title.Length > 45) throw new UserError($"Вопрос прогноза длиннее 45 символов ({title.Length}) — сократите");
        var outs = Clean(b.StrList("outcomes"));
        if (outs.Count < 2) throw new UserError("Нужно минимум 2 исхода");
        if (outs.Count > 10) throw new UserError("В прогнозе Twitch не больше 10 исходов");
        var longO = outs.FirstOrDefault(c => c.Length > 25);
        if (longO != null) throw new UserError($"Исход «{longO}» длиннее 25 символов — сократите");
        var win = Math.Clamp(b.Int("window", 120), 30, 1800);
        var p = await Call(() => Tw.Api(HttpMethod.Post, "/predictions", null, new
        {
            broadcaster_id = Tw.BroadcasterId, title,
            outcomes = outs.Select(o => new { title = o }).ToArray(), prediction_window = win,
        }), "Прогноз");
        OnPred(TwitchClient.First(p) as JsonObject);
        _viewedAt = ActionLog.Now;
        _hub.Log.Add("system", $"Запущен прогноз «{title}», ставки {FmtDur(win)}: {string.Join(" / ", outs)}");
        if (Cfg.Bool("chat_start"))
            _hub.Loop.Start(async () => { try { await Tw.SendChat($"🔮 Прогноз: {title} — делайте ставки баллами! Исходы: {string.Join(" / ", outs)}"); } catch { } });
        return "Прогноз запущен";
    }

    /// <summary>action: lock | resolve | cancel</summary>
    public async Task<string> UpdatePrediction(string action, string outcomeId)
    {
        NeedScope(PredScope, "прогнозы");
        var id = _pred?.Str("id") ?? "";
        if (id == "" || !IsActive(_pred)) throw new UserError("Нет активного прогноза");
        object body = action switch
        {
            "lock" => new { broadcaster_id = Tw.BroadcasterId, id, status = "LOCKED" },
            "cancel" => new { broadcaster_id = Tw.BroadcasterId, id, status = "CANCELED" },
            "resolve" => Outcomes(_pred).Any(o => o.Id == outcomeId)
                ? new { broadcaster_id = Tw.BroadcasterId, id, status = "RESOLVED", winning_outcome_id = outcomeId }
                : throw new UserError("Выберите исход-победитель"),
            _ => throw new UserError("Неизвестное действие"),
        };
        if (action == "lock" && _pred.Str("status") == "LOCKED") throw new UserError("Ставки уже закрыты");
        var p = await Call(() => Tw.Api(HttpMethod.Patch, "/predictions", null, body), "Прогноз");
        OnPred(TwitchClient.First(p) as JsonObject);
        return action switch { "lock" => "Ставки закрыты", "cancel" => "Прогноз отменён, баллы возвращены", _ => "Победитель выбран, баллы выплачены" };
    }

    // ---------- шаблоны ----------
    public void SaveTemplates(string kind, JsonNode list)
    {
        if (list is not JsonArray arr) throw new UserError("Неверный список шаблонов");
        var clean = new JsonArray();
        foreach (var t in arr.Take(30))
        {
            var title = t.Str("title").Trim();
            if (title == "") continue;
            var opts = Clean(t.StrList(kind == "poll" ? "choices" : "outcomes"));
            var o = new JsonObject { ["title"] = title };
            if (kind == "poll")
            {
                o["choices"] = new JsonArray(opts.Take(5).Select(x => (JsonNode)x).ToArray());
                o["duration"] = Math.Clamp(t.Int("duration", 120), 15, 1800);
            }
            else
            {
                o["outcomes"] = new JsonArray(opts.Take(10).Select(x => (JsonNode)x).ToArray());
                o["window"] = Math.Clamp(t.Int("window", 120), 30, 1800);
            }
            clean.Add(o);
        }
        _hub.S.Set(kind == "poll" ? "votes.poll_templates" : "votes.pred_templates", clean);
        _hub.Settings.Save();
    }

    // ---------- вспомогательное ----------
    private void NeedScope(string scope, string what)
    {
        if (!Tw.Streamer.Ready) throw new UserError("Сначала войдите в Twitch (раздел «Подключения»)");
        if (!Tw.Streamer.Scopes.Contains(scope))
            throw new UserError($"Нет разрешения на {what}: нажмите «Войти через Twitch» ещё раз и подтвердите новые права");
    }

    private async Task<JsonNode> Call(Func<Task<JsonNode>> f, string what)
    {
        try { return await f(); }
        catch (TwitchException e) { throw new UserError(Explain(e, what)); }
    }

    public static string Explain(TwitchException e, string what)
    {
        var r = (e.Reason ?? "").ToLowerInvariant();
        if (e.Status == 403 || r.Contains("partner") || r.Contains("affiliate"))
            return $"{what}: доступно только компаньонам (affiliate) и партнёрам Twitch";
        if (e.Status == 401) return $"{what}: нет разрешения — войдите в Twitch заново";
        if (r.Contains("already") || r.Contains("active"))
            return $"{what}: на канале уже идёт {(what == "Опрос" || what == "Опросы" ? "опрос" : "прогноз")} — сначала завершите его";
        if (r.Contains("not found")) return $"{what}: не найден на Twitch (возможно, уже завершён)";
        if (e.Status == 400) return $"{what}: Twitch отклонил запрос — {e.Reason}";
        return $"{what}: ошибка Twitch {e.Status} — {e.Reason}";
    }

    private static List<string> Clean(IEnumerable<string> xs) =>
        xs.Select(x => (x ?? "").Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static int Pct(double part, double total) => total <= 0 ? 0 : (int)Math.Round(part * 100 / total);
    private static string Fmt(double n) => ((long)n).ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
    private static string FmtDur(int s) => s % 60 == 0 ? $"{s / 60} мин" : s > 60 ? $"{s / 60} мин {s % 60} с" : $"{s} с";
    private static string Plural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100; var b = a % 10;
        if (a is > 10 and < 20) return many;
        return b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }
}
