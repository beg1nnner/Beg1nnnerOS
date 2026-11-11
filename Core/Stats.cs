using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Beg1nnnerOS.Core;

/// <summary>Локальная статистика чата и музыкальных заказов (тот же файл stats.sqlite, что в Python-версии).</summary>
public sealed class Stats
{
    private const double Week = 7 * 86400;
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public Stats(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec(@"CREATE TABLE IF NOT EXISTS chat(ts REAL, login TEXT, name TEXT, stream TEXT);
               CREATE INDEX IF NOT EXISTS chat_ts ON chat(ts);
               CREATE INDEX IF NOT EXISTS chat_stream ON chat(stream);
               CREATE TABLE IF NOT EXISTS music(ts REAL, stream TEXT, kind TEXT, status TEXT,
                                                login TEXT, name TEXT, title TEXT, cost INTEGER);
               CREATE INDEX IF NOT EXISTS music_ts ON music(ts);
               CREATE TABLE IF NOT EXISTS streams(id TEXT PRIMARY KEY, started REAL, ended REAL, title TEXT, category TEXT,
                                                  peak INTEGER, vsum INTEGER, vcount INTEGER);
               CREATE TABLE IF NOT EXISTS viewers(ts REAL, stream TEXT, n INTEGER);
               CREATE INDEX IF NOT EXISTS viewers_stream ON viewers(stream);
               CREATE TABLE IF NOT EXISTS events(ts REAL, stream TEXT, type TEXT, name TEXT, amount REAL, currency TEXT);
               CREATE INDEX IF NOT EXISTS events_stream ON events(stream);
               CREATE TABLE IF NOT EXISTS played(ts REAL, stream TEXT, vid TEXT, title TEXT, url TEXT, source TEXT,
                                                 requester TEXT, login TEXT, duration INTEGER, fallback INTEGER);
               CREATE INDEX IF NOT EXISTS played_ts ON played(ts);
               CREATE INDEX IF NOT EXISTS played_stream ON played(stream);");
    }

    private void Exec(string sql, params (string, object)[] args)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private List<object[]> Query(string sql, params (string, object)[] args)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var rows = new List<object[]>();
            while (r.Read())
            {
                var row = new object[r.FieldCount];
                r.GetValues(row);
                rows.Add(row);
            }
            return rows;
        }
    }

    private static (string, (string, object)[]) Where(string period, string stream)
    {
        if (period == "stream") return ("stream = $p", new (string, object)[] { ("$p", stream) });
        if (period == "week") return ("ts >= $p", new (string, object)[] { ("$p", ActionLog.Now - Week) });
        return ("1=1", Array.Empty<(string, object)>());
    }

    public void Chat(string login, string name, string stream) =>
        Exec("INSERT INTO chat VALUES($ts,$l,$n,$s)", ("$ts", ActionLog.Now), ("$l", login), ("$n", name), ("$s", stream));

    public List<object[]> TopChat(string period, string stream, int limit = 5)
    {
        var (w, a) = Where(period, stream);
        var list = new List<(string, object)>(a) { ("$lim", limit) };
        var rows = Query($"SELECT login, MAX(name), COUNT(*) c FROM chat WHERE {w} GROUP BY login ORDER BY c DESC LIMIT $lim",
            list.ToArray());
        var res = new List<object[]>();
        foreach (var r in rows)
            res.Add(new object[] { r[1] is string n && n.Length > 0 ? n : r[0]?.ToString(), Convert.ToInt64(r[2]) });
        return res;
    }

    public object ChatSummary(string period, string stream)
    {
        var (w, a) = Where(period, stream);
        var row = Query($"SELECT COUNT(*), COUNT(DISTINCT login) FROM chat WHERE {w}", a)[0];
        return new { messages = Convert.ToInt64(row[0]), users = Convert.ToInt64(row[1]), top = TopChat(period, stream, 10) };
    }

    public void Music(string stream, string kind, string status, string login, string name, string title, int cost = 0) =>
        Exec("INSERT INTO music VALUES($ts,$s,$k,$st,$l,$n,$t,$c)", ("$ts", ActionLog.Now), ("$s", stream), ("$k", kind),
            ("$st", status), ("$l", login), ("$n", name), ("$t", title), ("$c", cost));

    public object MusicSummary(string period, string stream)
    {
        var (w, a) = Where(period, stream);
        var by = new Dictionary<string, long>();
        foreach (var r in Query($"SELECT kind, status, COUNT(*) FROM music WHERE {w} GROUP BY kind, status", a))
            by[$"{r[0]}:{r[1]}"] = Convert.ToInt64(r[2]);
        var spent = Convert.ToInt64(Query($"SELECT COALESCE(SUM(cost),0) FROM music WHERE {w} AND status='ok'", a)[0][0]);
        var tracks = new List<object[]>();
        foreach (var r in Query($"SELECT title, COUNT(*) c FROM music WHERE {w} AND kind IN ('order','priority') AND status='ok' " +
                                "GROUP BY lower(title) ORDER BY c DESC LIMIT 10", a))
            tracks.Add(new object[] { r[0]?.ToString(), Convert.ToInt64(r[1]) });
        var users = new List<object[]>();
        foreach (var r in Query($"SELECT MAX(name), COUNT(*) c FROM music WHERE {w} AND kind IN ('order','priority') AND status='ok' " +
                                "GROUP BY login ORDER BY c DESC LIMIT 10", a))
            users.Add(new object[] { r[0]?.ToString(), Convert.ToInt64(r[1]) });
        long Get(string k) => by.TryGetValue(k, out var v) ? v : 0;
        long rejected = 0, skips = 0;
        foreach (var kv in by)
        {
            if (kv.Key.EndsWith(":rejected")) rejected += kv.Value;
            if ((kv.Key.StartsWith("skip:") || kv.Key.StartsWith("vote_skip:")) && kv.Key.EndsWith(":ok")) skips += kv.Value;
        }
        return new
        {
            orders = Get("order:ok") + Get("priority:ok") + Get("manual:ok"),
            rejected, skips, points = spent, top_tracks = tracks, top_users = users,
        };
    }

    // ---------- история сыгранных треков ----------
    public void Played(string stream, string vid, string title, string url, string source, string requester, string login,
                       int duration, bool fallback) =>
        Exec("INSERT INTO played VALUES($ts,$s,$v,$t,$u,$src,$r,$l,$d,$f)",
            ("$ts", ActionLog.Now), ("$s", stream), ("$v", vid), ("$t", title), ("$u", url), ("$src", source),
            ("$r", requester ?? ""), ("$l", login ?? ""), ("$d", duration), ("$f", fallback ? 1 : 0));

    /// <summary>История: scope = stream (текущий/указанный стрим) | today | week | all; поиск по названию и нику.</summary>
    public object History(string scope, string stream, string search, bool withFallback, int limit, int offset)
    {
        var where = new List<string>();
        var args = new List<(string, object)>();
        if (scope == "stream") { where.Add("stream=$s"); args.Add(("$s", stream)); }
        else if (scope == "today") { where.Add("ts >= $since"); args.Add(("$since", (double)new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds())); }
        else if (scope == "week") { where.Add("ts >= $since"); args.Add(("$since", ActionLog.Now - 7 * 86400.0)); }
        if (!withFallback) where.Add("fallback=0");
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(title LIKE $q OR requester LIKE $q OR login LIKE $q)");
            args.Add(("$q", "%" + search.Trim().Replace("%", "").Replace("_", "") + "%"));
        }
        var w = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var total = Scalar("SELECT COUNT(*) FROM played" + w, args.ToArray());
        var a2 = args.ToList();
        a2.Add(("$lim", Math.Clamp(limit, 1, 500)));
        a2.Add(("$off", Math.Max(0, offset)));
        var items = new List<object>();
        foreach (var r in Query("SELECT ts, title, url, source, requester, login, duration, fallback FROM played" + w +
                                " ORDER BY ts DESC LIMIT $lim OFFSET $off", a2.ToArray()))
            items.Add(new
            {
                ts = Convert.ToDouble(r[0]), title = r[1]?.ToString(), url = r[2]?.ToString(), source = r[3]?.ToString(),
                requester = r[4]?.ToString(), login = r[5]?.ToString(), duration = Convert.ToInt64(r[6]), fallback = Convert.ToInt64(r[7]) == 1,
            });
        return new { total, items };
    }

    /// <summary>Последние треки (для команды !история).</summary>
    public List<string> RecentTitles(int n)
    {
        var list = new List<string>();
        foreach (var r in Query("SELECT title FROM played WHERE fallback=0 ORDER BY ts DESC LIMIT $n", ("$n", n)))
            list.Add(r[0]?.ToString() ?? "");
        return list;
    }

    /// <summary>Самые частые треки стрима (для итогов).</summary>
    public List<(string Title, long Count)> TopPlayed(string stream, int n)
    {
        var list = new List<(string, long)>();
        foreach (var r in Query(@"SELECT title, COUNT(*) c FROM played WHERE stream=$s AND fallback=0
                                  GROUP BY title ORDER BY c DESC, MIN(ts) LIMIT $n", ("$s", stream), ("$n", n)))
            list.Add((r[0]?.ToString() ?? "", Convert.ToInt64(r[1])));
        return list;
    }

    // ---------- расширенная статистика стримов ----------
    /// <summary>Замер зрителей раз в минуту во время эфира; заодно создаёт запись о стриме.</summary>
    public void StreamSample(string id, double started, string title, string category, int viewers)
    {
        if (string.IsNullOrEmpty(id)) return;
        Exec(@"INSERT INTO streams(id, started, ended, title, category, peak, vsum, vcount) VALUES($id,$st,NULL,$t,$c,$v,$v,1)
               ON CONFLICT(id) DO UPDATE SET title=$t, category=$c, ended=NULL, peak=MAX(peak,$v), vsum=vsum+$v, vcount=vcount+1",
            ("$id", id), ("$st", started), ("$t", title ?? ""), ("$c", category ?? ""), ("$v", viewers));
        Exec("INSERT INTO viewers VALUES($ts,$s,$n)", ("$ts", ActionLog.Now), ("$s", id), ("$n", viewers));
    }

    public void StreamEnded(string id) =>
        Exec("UPDATE streams SET ended=$e WHERE id=$id AND ended IS NULL", ("$e", ActionLog.Now), ("$id", id));

    public void Event(string stream, string type, string name, double amount, string currency) =>
        Exec("INSERT INTO events VALUES($ts,$s,$t,$n,$a,$c)", ("$ts", ActionLog.Now), ("$s", stream), ("$t", type),
            ("$n", name ?? ""), ("$a", amount), ("$c", currency ?? ""));

    private long Scalar(string sql, params (string, object)[] args)
    {
        var rows = Query(sql, args);
        return rows.Count == 0 || rows[0][0] is DBNull ? 0 : Convert.ToInt64(rows[0][0]);
    }

    private Dictionary<string, double> Donations(string stream)
    {
        var d = new Dictionary<string, double>();
        foreach (var r in Query("SELECT currency, SUM(amount) FROM events WHERE stream=$s AND type='donation' GROUP BY currency", ("$s", stream)))
            d[r[0]?.ToString() ?? ""] = Convert.ToDouble(r[1]);
        return d;
    }

    private Dictionary<string, object> Summary(object[] row)
    {
        var id = row[0].ToString();
        var started = Convert.ToDouble(row[1]);
        var ended = row[2] is DBNull ? (double?)null : Convert.ToDouble(row[2]);
        var vcount = Convert.ToInt64(row[7]);
        long Ev(string type) => Scalar("SELECT COUNT(*) FROM events WHERE stream=$s AND type=$t", ("$s", id), ("$t", type));
        long EvSum(string type) => Scalar("SELECT COALESCE(SUM(amount),0) FROM events WHERE stream=$s AND type=$t", ("$s", id), ("$t", type));
        return new Dictionary<string, object>
        {
            ["id"] = id, ["started"] = started, ["ended"] = ended, ["title"] = row[3]?.ToString() ?? "",
            ["category"] = row[4]?.ToString() ?? "",
            ["duration"] = (ended ?? ActionLog.Now) - started,
            ["peak"] = Convert.ToInt64(row[5]),
            ["avg"] = vcount > 0 ? Math.Round(Convert.ToDouble(row[6]) / vcount) : 0,
            ["messages"] = Scalar("SELECT COUNT(*) FROM chat WHERE stream=$s", ("$s", id)),
            ["chatters"] = Scalar("SELECT COUNT(DISTINCT login) FROM chat WHERE stream=$s", ("$s", id)),
            ["follows"] = Ev("follow"),
            ["subs"] = EvSum("sub"),
            ["bits"] = EvSum("cheer"),
            ["raids"] = Ev("raid"),
            ["donations"] = Donations(id),
            ["orders"] = Scalar("SELECT COUNT(*) FROM music WHERE stream=$s AND kind IN ('order','priority') AND status='ok'", ("$s", id)),
            ["points"] = Scalar("SELECT COALESCE(SUM(cost),0) FROM music WHERE stream=$s AND status='ok'", ("$s", id)),
        };
    }

    /// <summary>Стримы за последние days дней (новые сверху) и итоги за период.</summary>
    public object Streams(int days)
    {
        var since = ActionLog.Now - Math.Max(1, days) * 86400.0;
        var list = new List<Dictionary<string, object>>();
        foreach (var r in Query("SELECT id, started, ended, title, category, peak, vsum, vcount FROM streams WHERE started >= $s ORDER BY started DESC",
                     ("$s", since)))
            list.Add(Summary(r));
        double hours = 0, avgSum = 0;
        long peak = 0, messages = 0, follows = 0, subs = 0, bits = 0, orders = 0;
        var donations = new Dictionary<string, double>();
        foreach (var s in list)
        {
            hours += (double)s["duration"] / 3600;
            peak = Math.Max(peak, (long)s["peak"]);
            avgSum += Convert.ToDouble(s["avg"]);
            messages += (long)s["messages"];
            follows += (long)s["follows"];
            subs += (long)s["subs"];
            bits += (long)s["bits"];
            orders += (long)s["orders"];
            foreach (var kv in (Dictionary<string, double>)s["donations"])
                donations[kv.Key] = donations.GetValueOrDefault(kv.Key) + kv.Value;
        }
        return new
        {
            streams = list,
            total = new
            {
                count = list.Count, hours = Math.Round(hours, 1), peak, avg = list.Count > 0 ? Math.Round(avgSum / list.Count) : 0,
                messages, follows, subs, bits, orders, donations,
            },
        };
    }

    /// <summary>Данные для итогов стрима: сводка + топ чата + треки стрима.</summary>
    public JsonObject SummaryData(string id)
    {
        var rows = Query("SELECT id, started, ended, title, category, peak, vsum, vcount FROM streams WHERE id=$id", ("$id", id));
        if (rows.Count == 0) return null;
        var d = (JsonObject)System.Text.Json.JsonSerializer.SerializeToNode(Summary(rows[0]));
        var chat = new JsonArray();
        foreach (var r in TopChat("stream", id, 3)) chat.Add(new JsonArray(r[0]?.ToString(), Convert.ToInt64(r[1])));
        d["top_chat"] = chat;
        var tracks = new JsonArray();
        foreach (var (t, _) in TopPlayed(id, 5)) tracks.Add(t);
        d["top_tracks"] = tracks;
        return d;
    }

    /// <summary>Подробности стрима: график зрителей, топ чата и заказов, лента событий.</summary>
    public object StreamDetails(string id)
    {
        var rows = Query("SELECT id, started, ended, title, category, peak, vsum, vcount FROM streams WHERE id=$id", ("$id", id));
        if (rows.Count == 0) return null;
        var viewers = new List<object[]>();
        foreach (var r in Query("SELECT ts, n FROM viewers WHERE stream=$s ORDER BY ts", ("$s", id)))
            viewers.Add(new object[] { Convert.ToDouble(r[0]), Convert.ToInt64(r[1]) });
        var events = new List<object>();
        foreach (var r in Query("SELECT ts, type, name, amount, currency FROM events WHERE stream=$s ORDER BY ts DESC LIMIT 300", ("$s", id)))
            events.Add(new { ts = Convert.ToDouble(r[0]), type = r[1]?.ToString(), name = r[2]?.ToString(), amount = Convert.ToDouble(r[3]), currency = r[4]?.ToString() });
        var requesters = new List<object[]>();
        foreach (var r in Query(@"SELECT MAX(name), COUNT(*) c FROM music WHERE stream=$s AND kind IN ('order','priority') AND status='ok'
                                  GROUP BY login ORDER BY c DESC LIMIT 10", ("$s", id)))
            requesters.Add(new object[] { r[0]?.ToString(), Convert.ToInt64(r[1]) });
        return new
        {
            summary = Summary(rows[0]), viewers, events, top_chat = TopChat("stream", id, 10), top_requesters = requesters,
        };
    }
}
