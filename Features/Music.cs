using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Features;

public sealed class Track
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
    public string Vid = "";
    public string Title = "";
    public string PageUrl = "";
    public int Duration;
    public string Requester = "";
    public string Login = "";
    public int Priority;
    public string Source = "youtube";
    public long Views = -1;   // просмотры на YouTube (-1 — неизвестно)
    public (string RewardId, string RedemptionId, int Cost)? Redemption;

    public object Public() => new
    {
        id = Id, vid = Vid, title = Title, page_url = PageUrl, duration = Duration, requester = Requester,
        login = Login, priority = Priority, source = Source, paid = Redemption != null, views = Views,
    };
}

public sealed class ResolveException : Exception
{
    public ResolveException(string message) : base(message) { }
}

/// <summary>Музыка: поиск (YouTube / Яндекс / Spotify), очередь с приоритетами, локальный плеер.</summary>
public sealed class Music
{
    private static readonly Regex YtRe = new(@"^https?://(www\.|m\.|music\.)?(youtube\.com|youtu\.be)/", RegexOptions.IgnoreCase);
    private static readonly Regex SpotifyRe = new(@"^https?://open\.spotify\.com/(intl-[\w-]+/)?track/", RegexOptions.IgnoreCase);
    private static readonly Regex UrlRe = new(@"^https?://", RegexOptions.IgnoreCase);

    private readonly Hub _hub;
    public readonly List<Track> Queue = new();
    public Track Current;
    public readonly HashSet<string> Votes = new();
    public readonly Player Player = new();
    public readonly Chime Chime = new();
    private TaskCompletionSource<bool> _wake = new();
    public int ToastId;
    public bool Loading;
    // Загрузки по ID трека: очередь скачивается заранее, чтобы следующий трек начинался сразу
    private readonly Dictionary<string, Task<string>> _downloads = new();
    public string ToastText = "";

    /// <summary>Замена поиска в тестах.</summary>
    public Func<string, Task<Track>> ResolveOverride;

    public Music(Hub hub) { _hub = hub; }

    private JsonObject Cfg => _hub.S.Obj("music");

    public void ApplySettings()
    {
        var v = Math.Max(0, Math.Min(100, Cfg.Int("volume", 60))) / 100.0;
        Player.Volume = (float)(v * v);
        Player.DeviceId = Cfg.Str("audio_device");
    }

    public void Wake() => _wake.TrySetResult(true);

    public void ShowToast(string text)
    {
        ToastId++;
        ToastText = text;
    }

    public async Task Run()
    {
        ApplySettings();
        Directory.CreateDirectory(Config.CacheDir);
        CleanupCache();
        while (true)
        {
            await _wake.Task;
            _wake = new TaskCompletionSource<bool>();
            if (Current == null && Queue.Count > 0) await PlayNext();
        }
    }

    private static void CleanupCache()
    {
        try
        {
            foreach (var f in new DirectoryInfo(Config.CacheDir).GetFiles())
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) f.Delete();
        }
        catch { }
    }

    private async Task<string> DownloadAudio(Track t)
    {
        Directory.CreateDirectory(Config.CacheDir);
        if (t.Vid.StartsWith("ym:"))
        {
            var path = Path.Combine(Config.CacheDir, "ym_" + t.Vid[3..] + ".mp3");
            if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
            var url = await _hub.Yandex.DirectLink(t.Vid[3..]);
            var bytes = await _hub.Http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        return await _hub.YtDlp.DownloadAudio(t.Vid, Config.CacheDir);
    }

    private async Task PlayNext()
    {
        var t = Queue[0];
        Queue.RemoveAt(0);
        Current = t;
        Votes.Clear();
        string path;
        Loading = true;
        try
        {
            if (!t.Vid.StartsWith("ym:") && _hub.YtDlp.Installing)
                _hub.Log.Add("music", "Готовлю плеер к первому запуску (загрузка yt-dlp)…");
            path = await GetAudio(t);
        }
        catch (Exception e)
        {
            _downloads.Remove(t.Vid);
            var reason = Reason(e);
            _hub.Log.Error($"Не удалось загрузить «{t.Title}»: {reason}");
            Current = null;
            Wake();
            await TellViewer(t, reason);
            return;
        }
        finally { Loading = false; }
        if (Current != t) return;   // пропущен во время загрузки
        try
        {
            Player.Play(path, OnEnd);
        }
        catch (Exception e)
        {
            _hub.Log.Error($"Плеер не смог открыть «{t.Title}»: файл повреждён или формат не поддерживается ({e.Message})");
            Current = null;
            Wake();
            await TellViewer(t, "не удалось открыть звук этого трека");
            return;
        }
        _hub.Log.Add("music", $"Играет: {t.Title}" + (t.Requester != "" ? $" (заказ {t.Requester})" : ""));
        ShowToast($"♪ {t.Title}");
        Prefetch();
    }

    /// <summary>Понятная причина ошибки на русском.</summary>
    public static string Reason(Exception e) => e is ResolveException ? e.Message : YtDlp.Explain(e.Message);

    /// <summary>Сообщает зрителю в чат, что его трек не удалось включить.</summary>
    private async Task TellViewer(Track t, string reason)
    {
        if (t.Login == "" || !Cfg.Bool("chat_errors", true)) return;
        var title = t.Title.Length > 80 ? t.Title[..80] + "…" : t.Title;
        try { await _hub.Twitch.SendChat($"@{t.Requester}, не удалось включить «{title}»: {reason}. Трек пропущен"); }
        catch (Exception e) { _hub.Log.Add("skip", $"Не удалось написать в чат: {e.Message}"); }
    }

    /// <summary>Мягкий сигнал о новом заказе (настройки «Музыка → Звук при заказе»).</summary>
    public void PlayChime(bool force = false)
    {
        if (!force && !Cfg.Bool("order_sound", true)) return;
        try { Chime.Play(Cfg.Str("order_sound_style", "bell"), Cfg.Int("order_sound_volume", 50), Cfg.Str("audio_device"), force); }
        catch (Exception e) { _hub.Log.Add("skip", $"Звук заказа: {e.Message}"); }
    }

    private Task<string> GetAudio(Track t)
    {
        if (_downloads.TryGetValue(t.Vid, out var task) && !task.IsFaulted && !task.IsCanceled) return task;
        task = DownloadAudio(t);
        _downloads[t.Vid] = task;
        return task;
    }

    /// <summary>Заранее скачивает два следующих трека очереди.</summary>
    private void Prefetch()
    {
        foreach (var t in Queue.Take(2).ToList())
        {
            if (_downloads.ContainsKey(t.Vid)) continue;
            var task = GetAudio(t);
            var vid = t.Vid;
            _ = task.ContinueWith(x =>
            {
                _hub.Log.Add("skip", $"Предзагрузка «{t.Title}» не удалась: {x.Exception?.GetBaseException().Message}");
                _hub.Loop.Post(() => _downloads.Remove(vid));
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        // не держим в памяти задачи давно сыгранных треков
        if (_downloads.Count > 30)
        {
            var keep = new HashSet<string>(Queue.Select(q => q.Vid));
            if (Current != null) keep.Add(Current.Vid);
            foreach (var k in _downloads.Keys.Where(k => !keep.Contains(k)).ToList()) _downloads.Remove(k);
        }
    }

    private void OnEnd(int gen, string error)
    {
        if (gen != Player.Gen) return;
        if (error != "") _hub.Log.Error($"Плеер: воспроизведение прервалось ({error})");
        Current = null;
        Wake();
    }

    // ---------- управление ----------
    public bool Skip(string reason = "")
    {
        if (Current == null) return false;
        var title = Current.Title;
        Player.Stop();
        Current = null;
        _hub.Log.Add("music", $"Пропущен: {title}" + (reason != "" ? $" ({reason})" : ""));
        Wake();
        return true;
    }

    public bool TogglePause()
    {
        Player.SetPaused(!Player.Paused);
        return Player.Paused;
    }

    public void SetVolume(int v)
    {
        Cfg["volume"] = Math.Max(0, Math.Min(100, v));
        _hub.Settings.Save();
        ApplySettings();
    }

    public Track Remove(string id)
    {
        var t = Queue.FirstOrDefault(x => x.Id == id);
        if (t != null) Queue.Remove(t);
        return t;
    }

    public void Move(string id, int delta)
    {
        var i = Queue.FindIndex(x => x.Id == id);
        if (i < 0) return;
        var t = Queue[i];
        Queue.RemoveAt(i);
        Queue.Insert(Math.Max(0, Math.Min(Queue.Count, i + delta)), t);
    }

    public string VoteSkip(ChatMsg msg)
    {
        var cur = Current;
        if (cur == null) return "Сейчас ничего не играет";
        if (!Cfg.Bool("skip_vote_enabled", true) && !msg.IsMod) return "Голосование за пропуск выключено";
        if (msg.IsMod || (cur.Login != "" && msg.Login == cur.Login))
        {
            Skip($"!skip от {msg.Login}");
            _hub.Stats.Music(_hub.Stream.Key, "vote_skip", "ok", msg.Login, msg.Name, cur.Title);
            return $"Трек «{cur.Title}» пропущен";
        }
        var need = Math.Max(1, Cfg.Int("skip_votes", 3));
        if (Votes.Contains(msg.Login)) return $"{msg.Name}, вы уже голосовали ({Votes.Count}/{need})";
        Votes.Add(msg.Login);
        if (Votes.Count >= need)
        {
            Skip("голосование");
            _hub.Stats.Music(_hub.Stream.Key, "vote_skip", "ok", msg.Login, msg.Name, cur.Title);
            return $"Голосование: трек «{cur.Title}» пропущен";
        }
        return $"Голос за пропуск принят ({Votes.Count}/{need})";
    }

    // ---------- поиск ----------
    private async Task<string> SpotifyQuery(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        using var resp = await _hub.Http.SendAsync(req);
        var page = await resp.Content.ReadAsStringAsync();
        var m = Regex.Match(page, "<title>(.*?)</title>", RegexOptions.Singleline);
        var title = m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        title = Regex.Replace(title, @"\s*\|\s*Spotify\s*$", "");
        title = Regex.Replace(title, @"\s+-\s+song( and lyrics)? by\s+", " ", RegexOptions.IgnoreCase);
        if (title.Trim() == "") throw new ResolveException("не удалось прочитать трек Spotify");
        return title.Trim();
    }

    private List<string> SearchOrder()
    {
        var src = Cfg.Obj("sources");
        var order = Cfg.Str("search_first") == "yandex" ? new[] { "yandex", "youtube" } : new[] { "youtube", "yandex" };
        return order.Where(s => (s == "youtube" && src.Bool("search")) ||
                                (s == "yandex" && src.Bool("yandex") && _hub.Yandex.Token != "")).ToList();
    }

    private static Track FromYandex(YandexTrack y, string source)
    {
        if (!y.Available) throw new ResolveException("трек недоступен в Яндекс Музыке");
        return new Track { Vid = "ym:" + y.Id, Title = y.Title, PageUrl = y.Url, Duration = y.Duration, Source = source };
    }

    private Task<Track> YtByUrl(string url, string source) => _hub.YtDlp.ByUrl(url, source);

    private Task<Track> YtSearch(string q, string source, long minViews) => _hub.YtDlp.Search(q, source, minViews);

    private static string Short(string s) => s.Length > 120 ? s[..120] : s;

    private async Task<Track> SearchAny(string q, string source, long minViews)
    {
        var order = SearchOrder();
        if (order.Count == 0) throw new ResolveException("поиск по названию отключён, пришлите ссылку");
        var errors = new List<string>();
        foreach (var s in order)
        {
            try
            {
                return s == "yandex" ? FromYandex(await _hub.Yandex.Search(q), source) : await YtSearch(q, source, minViews);
            }
            catch (Exception e)   // пробуем следующий источник
            {
                var why = e switch
                {
                    YandexException or ResolveException => e.Message,
                    HttpRequestException or TaskCanceledException when s == "yandex" => "нет связи с Яндекс Музыкой",
                    _ when s == "yandex" => "ошибка ответа Яндекс Музыки",
                    _ => YtDlp.Explain(e.Message),
                };
                if (e is not (YandexException or ResolveException)) _hub.Log.Add("skip", $"Поиск ({s}): {Short(e.Message)}");
                errors.Add(order.Count > 1 ? (s == "yandex" ? "Яндекс Музыка: " : "YouTube: ") + why : why);
            }
        }
        throw new ResolveException(string.Join("; ", errors));
    }

    public async Task<Track> Resolve(string query, long minViews = 0)
    {
        if (ResolveOverride != null) return await ResolveOverride(query);
        var q = query.Trim();
        var src = Cfg.Obj("sources");
        if (SpotifyRe.IsMatch(q))
        {
            if (!src.Bool("spotify")) throw new ResolveException("ссылки Spotify отключены");
            return await SearchAny(await SpotifyQuery(q), "spotify", minViews);
        }
        var ym = YandexMusic.TrackRe.Match(q);
        if (ym.Success)
        {
            if (!src.Bool("yandex")) throw new ResolveException("ссылки Яндекс Музыки отключены");
            try { return FromYandex(await _hub.Yandex.Track(ym.Groups[1].Value), "yandex"); }
            catch (ResolveException) { throw; }
            catch (Exception e) { throw new ResolveException($"Яндекс Музыка: {e.Message}"); }
        }
        if (YtRe.IsMatch(q))
        {
            if (!src.Bool("youtube")) throw new ResolveException("ссылки YouTube отключены");
            return await YtByUrl(q, "youtube");
        }
        if (UrlRe.IsMatch(q)) throw new ResolveException("поддерживаются ссылки YouTube, Яндекс Музыки, Spotify или название трека");
        return await SearchAny(q, "search", minViews);
    }

    private string Banned(string text)
    {
        var low = text.ToLowerInvariant();
        return Cfg.StrList("banned_words").FirstOrDefault(w => w != "" && low.Contains(w.ToLowerInvariant()));
    }

    public async Task<(bool Ok, string Text, Track Track)> Request(string query, string name, string login,
        int priority = 0, bool manual = false, (string, string, int)? redemption = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return (false, "пустой заказ: укажите ссылку или название", null);
        if (!manual)
        {
            var blocked = Cfg.StrList("blocked_users").Select(u => u.ToLowerInvariant().TrimStart('@'));
            if (blocked.Contains(login.ToLowerInvariant())) return (false, "заказы музыки для вас ограничены", null);
            if (Banned(query) != null) return (false, "запрещённое слово в заказе", null);
            if (Queue.Count >= Cfg.Int("queue_limit", 15)) return (false, $"очередь заполнена ({Cfg.Int("queue_limit")})", null);
            if (Queue.Count(t => t.Login == login) >= Cfg.Int("per_user_limit", 2))
                return (false, $"лимит заказов от зрителя: {Cfg.Int("per_user_limit")}", null);
        }
        var minViews = manual ? 0L : (long)Cfg.Num("min_views", 0);
        Track track;
        try { track = await Resolve(query, minViews); }
        catch (ResolveException e) { return (false, e.Message, null); }
        catch (Exception e) { return (false, $"не удалось найти трек: {YtDlp.Explain(e.Message)}", null); }
        if (!manual)
        {
            var maxSec = Cfg.Int("max_duration_min", 8) * 60;
            if (track.Duration <= 0) return (false, "не удалось определить длительность", null);
            if (track.Duration > maxSec) return (false, $"трек длиннее {Cfg.Int("max_duration_min")} мин", null);
            if (Banned(track.Title) != null) return (false, "запрещённое слово в названии трека", null);
            // Фильтр просмотров — только для YouTube (у Яндекс Музыки просмотров нет)
            if (minViews > 0 && !track.Vid.StartsWith("ym:") && track.Views >= 0 && track.Views < minViews)
                return (false, $"у видео меньше {YtDlp.FormatViews(minViews)} просмотров", null);
            var bt = Cfg.StrList("blocked_tracks").Select(b => b.ToLowerInvariant()).ToList();
            if (bt.Contains(track.Vid.ToLowerInvariant()) || bt.Contains(track.Title.ToLowerInvariant()) ||
                bt.Contains(track.PageUrl.ToLowerInvariant()))
                return (false, "этот трек заблокирован", null);
        }
        track.Requester = name;
        track.Login = login;
        track.Priority = priority;
        track.Redemption = redemption;
        var idx = Queue.FindIndex(x => x.Priority < priority);
        if (idx < 0) idx = Queue.Count;
        Queue.Insert(idx, track);
        Wake();
        Prefetch();
        if (!manual) PlayChime();
        return (true, $"«{track.Title}» добавлен в очередь (позиция {idx + 1})", track);
    }

    public object State()
    {
        var cur = Current;
        object curObj = null;
        if (cur != null)
            curObj = new
            {
                id = cur.Id, vid = cur.Vid, title = cur.Title, page_url = cur.PageUrl, duration = cur.Duration,
                requester = cur.Requester, login = cur.Login, priority = cur.Priority, source = cur.Source,
                paid = cur.Redemption != null, position = Math.Round(Player.Position, 1), paused = Player.Paused,
                loading = Loading, views = cur.Views,
            };
        return new
        {
            current = curObj,
            queue = Queue.Select(t => t.Public()).ToList(),
            volume = Cfg.Int("volume", 60),
            votes = Votes.Count,
            votes_needed = Cfg.Int("skip_votes", 3),
            toast = new { id = ToastId, text = ToastText },
        };
    }
}
