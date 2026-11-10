using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Features;

/// <summary>
/// YouTube через yt-dlp. Используется распакованная сборка yt-dlp_win.zip: в отличие от
/// однофайлового yt-dlp.exe ей не нужно распаковывать себя при каждом запуске, поэтому вызов быстрее.
/// Скачивается при первом запуске и раз в сутки проверяет новую версию на GitHub.
/// </summary>
public sealed class YtDlp
{
    private const string ZipUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_win.zip";
    private const string LatestUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest";
    private readonly Hub _hub;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private int _running;
    public string Status = "не загружен";
    public bool Installing;

    public YtDlp(Hub hub) { _hub = hub; }

    private static string ToolsDir => Path.Combine(Config.Root, "tools");   // общий для всех профилей
    private static string InstallDir => Path.Combine(ToolsDir, "yt-dlp");
    private static string ExePath => Path.Combine(InstallDir, "yt-dlp.exe");
    private static string VersionPath => Path.Combine(ToolsDir, "yt-dlp.version");
    private static string StampPath => Path.Combine(ToolsDir, "yt-dlp.checked");
    private static string BundledPath => Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");

    public bool Ready => File.Exists(BundledPath) || File.Exists(ExePath);

    /// <summary>Путь к yt-dlp.exe: рядом с программой (если положили вручную) или в профиле.</summary>
    public async Task<string> EnsureExe()
    {
        if (File.Exists(BundledPath)) return BundledPath;
        if (File.Exists(ExePath)) return ExePath;
        await _installLock.WaitAsync();
        try
        {
            if (File.Exists(ExePath)) return ExePath;
            await Install(await LatestVersion());
            return ExePath;
        }
        catch (Exception e) when (e is not ResolveException)
        {
            Status = "не удалось загрузить: " + e.Message;
            throw new ResolveException("не удалось загрузить yt-dlp (нужен доступ к github.com). " +
                                       "Можно скачать yt-dlp.exe вручную и положить рядом с Beg1nnnerOS.exe");
        }
        finally { _installLock.Release(); }
    }

    private async Task<string> LatestVersion()
    {
        try
        {
            using var resp = await _hub.Http.GetAsync(LatestUrl, HttpCompletionOption.ResponseHeadersRead);
            var uri = resp.RequestMessage?.RequestUri?.ToString() ?? "";
            var i = uri.LastIndexOf("/tag/", StringComparison.Ordinal);
            return i >= 0 ? uri[(i + 5)..] : "";
        }
        catch { return ""; }
    }

    private static string CurrentVersion() => File.Exists(VersionPath) ? File.ReadAllText(VersionPath).Trim() : "";

    private async Task Install(string version)
    {
        Installing = true;
        Status = "загрузка yt-dlp…";
        _hub.Log.Add("system", "Загрузка yt-dlp с GitHub" + (version != "" ? $" (версия {version})" : "") + "…");
        try
        {
            Directory.CreateDirectory(ToolsDir);
            var zip = Path.Combine(ToolsDir, "yt-dlp_win.zip.download");
            using (var resp = await _hub.Http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zip);
                await resp.Content.CopyToAsync(fs);
            }
            var tmpDir = InstallDir + ".new";
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, tmpDir));
            File.Delete(zip);
            var exe = Directory.GetFiles(tmpDir, "yt-dlp.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new IOException("в архиве нет yt-dlp.exe");
            var root = Path.GetDirectoryName(exe);
            if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true);
            Directory.Move(root, InstallDir);
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
            File.WriteAllText(VersionPath, version);
            File.WriteAllText(StampPath, DateTime.UtcNow.ToString("o"));
            // старый однофайловый вариант из прошлой версии программы больше не нужен
            try { File.Delete(Path.Combine(ToolsDir, "yt-dlp.exe")); } catch { }
            Status = "готов" + (version != "" ? $" ({version})" : "");
            _hub.Log.Add("system", "yt-dlp готов");
        }
        finally { Installing = false; }
    }

    /// <summary>Раз в сутки — проверка новой версии yt-dlp (YouTube часто меняется).</summary>
    public async Task UpdateIfNeeded()
    {
        try
        {
            var exe = await EnsureExe();
            if (exe == BundledPath) { Status = "готов (рядом с программой)"; return; }
            Status = "готов" + (CurrentVersion() != "" ? $" ({CurrentVersion()})" : "");
            if (File.Exists(StampPath) &&
                DateTime.TryParse(File.ReadAllText(StampPath), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromDays(1))
                return;
            var latest = await LatestVersion();
            File.WriteAllText(StampPath, DateTime.UtcNow.ToString("o"));
            if (latest == "" || latest == CurrentVersion()) return;
            // ждём, пока не работает ни один процесс yt-dlp (не дольше 10 минут)
            for (var i = 0; i < 600 && _running > 0; i++) await Task.Delay(1000);
            if (_running > 0) return;
            await _installLock.WaitAsync();
            try { await Install(latest); }
            finally { _installLock.Release(); }
        }
        catch (Exception e) { _hub.Log.Add("skip", "yt-dlp: " + e.Message); }
    }

    /// <summary>Запуск yt-dlp; при обрыве защищённого соединения — ещё одна попытка через 2 с.</summary>
    private async Task<string> Run(IEnumerable<string> args, TimeSpan timeout)
    {
        var list = args.ToList();
        try { return await RunOnce(list, timeout); }
        catch (ResolveException e) when (e.Message.StartsWith("соединение с YouTube обрывается"))
        {
            await Task.Delay(2000);
            return await RunOnce(list, timeout);
        }
    }

    /// <summary>Один запуск yt-dlp; возвращает stdout. Ошибка — ResolveException с текстом из stderr.</summary>
    private async Task<string> RunOnce(IEnumerable<string> args, TimeSpan timeout)
    {
        var exe = await EnsureExe();
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe),
        };
        // повторы спасают от разовых обрывов соединения (SSL, таймауты) по пути до YouTube
        foreach (var a in new[] { "--encoding", "utf-8", "--no-warnings", "--extractor-retries", "3", "--retries", "5", "--socket-timeout", "20" }.Concat(args)) psi.ArgumentList.Add(a);
        _running++;
        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw new ResolveException("YouTube не ответил вовремя");
            }
            var stdout = await outTask;
            var stderr = await errTask;
            if (p.ExitCode != 0)
            {
                var lines = stderr.Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
                var line = lines.LastOrDefault(l => l.StartsWith("ERROR")) ?? lines.LastOrDefault() ?? "ошибка yt-dlp";
                line = line.Replace("ERROR: ", "");
                _hub.Log.Add("skip", "yt-dlp: " + (line.Length > 300 ? line[..300] : line));   // оригинал — для диагностики
                throw new ResolveException(Explain(line));
            }
            return stdout;
        }
        finally { _running--; }
    }

    // Известные ошибки YouTube / yt-dlp → понятный текст на русском (для чата и журнала).
    // Порядок важен: сначала более точные причины.
    private static readonly (string[] Keys, string Text)[] KnownErrors =
    {
        (new[] { "confirm your age", "age-restricted", "age restricted", "inappropriate for some users" },
            "видео с ограничением по возрасту (18+), YouTube не даёт включить его без входа в аккаунт"),
        (new[] { "not a bot" }, "YouTube попросил подтвердить, что запрос не от бота. Попробуйте чуть позже"),
        (new[] { "private video", "video is private" }, "это приватное видео"),
        (new[] { "members-only", "members only", "join this channel" }, "видео доступно только спонсорам канала"),
        (new[] { "copyright" }, "видео заблокировано по жалобе правообладателя"),
        (new[] { "removed by the uploader", "has been removed", "has been terminated", "no longer available" }, "видео удалено"),
        (new[] { "not available in your country", "blocked it in your country", "geo restrict", "geo-restrict" },
            "видео недоступно в нашей стране"),
        (new[] { "premieres in", "premiere will begin", "live event will begin", "this live event" }, "премьера или эфир ещё не начались"),
        (new[] { "is not a valid url", "unsupported url", "incomplete youtube id", "invalid video id" }, "неверная ссылка на видео"),
        (new[] { "requested format is not available", "no video formats found", "only images are available" },
            "у этого видео не удалось получить звук"),
        (new[] { "http error 429", "too many requests" }, "YouTube временно ограничил запросы. Попробуйте позже"),
        (new[] { "http error 403", "forbidden" }, "YouTube не отдал файл. Попробуйте ещё раз"),
        (new[] { "unable to extract", "nsig", "signature" }, "YouTube что-то изменил, yt-dlp скоро обновится. Попробуйте позже"),
        (new[] { "ssl", "certificate verify", "eof occurred", "connection reset", "remote end closed" },
            "соединение с YouTube обрывается: мешает VPN, антивирус (проверка HTTPS) или провайдер"),
        (new[] { "failed to resolve", "no such host", "timed out", "timeout", "getaddrinfo", "connection", "network is unreachable", "urlopen error" }, "нет связи с YouTube"),
        (new[] { "video unavailable", "is unavailable", "not available" }, "видео недоступно"),
    };

    /// <summary>Переводит ошибку на русский. Русский текст возвращается как есть.</summary>
    public static string Explain(string error)
    {
        var e = (error ?? "").Trim();
        if (e == "") return "не удалось загрузить трек";
        var low = e.ToLowerInvariant();
        foreach (var (keys, text) in KnownErrors)
            if (keys.Any(low.Contains)) return text;
        if (e.Any(ch => ch is >= 'А' and <= 'я' or 'ё' or 'Ё')) return e.Length > 160 ? e[..160] : e;
        return "не удалось загрузить трек";
    }

    private static Track ToTrack(JsonNode info, string source, string fallbackTitle)
    {
        var id = info.Str("id");
        var url = info.Str("webpage_url");
        if (url == "") url = $"https://www.youtube.com/watch?v={id}";
        return new Track
        {
            Vid = id, Title = info.Str("title", fallbackTitle), PageUrl = url,
            Duration = (int)info.Num("duration"), Source = source, Views = (long)info.Num("view_count", -1),
        };
    }

    private static bool IsLive(JsonNode info) =>
        info.Bool("is_live") || info.Str("live_status") is "is_live" or "is_upcoming";

    public static string FormatViews(long n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Сведения о видео по ссылке.</summary>
    public async Task<Track> ByUrl(string url, string source)
    {
        var json = await Run(new[] { "-j", "--no-playlist", url }, TimeSpan.FromSeconds(60));
        var line = json.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("{"));
        if (line == null) throw new ResolveException("ничего не найдено");
        var info = J.ParseLoose(line);
        if (IsLive(info)) throw new ResolveException("прямые трансляции не поддерживаются");
        return ToTrack(info, source, url);
    }

    /// <summary>Список видео плейлиста (без загрузки страниц каждого видео), до 500 штук.</summary>
    public async Task<List<Track>> Playlist(string url)
    {
        if (!url.Contains("list=") && !url.Contains("/playlist"))
            throw new ResolveException("это не ссылка на плейлист YouTube (в ней должно быть «list=»)");
        var json = await Run(new[] { "--flat-playlist", "-J", "--playlist-end", "500", url }, TimeSpan.FromSeconds(90));
        var start = json.IndexOf('{');
        if (start < 0) throw new ResolveException("плейлист не найден");
        var info = J.ParseLoose(json[start..]);
        var list = new List<Track>();
        foreach (var e in info.Arr("entries").OfType<JsonNode>())
        {
            var id = e.Str("id");
            var title = e.Str("title");
            if (id == "" || title is "" or "[Private video]" or "[Deleted video]") continue;
            if (IsLive(e)) continue;
            list.Add(new Track
            {
                Vid = id, Title = title, PageUrl = $"https://www.youtube.com/watch?v={id}",
                Duration = (int)e.Num("duration"), Source = "youtube",
            });
        }
        if (list.Count == 0) throw new ResolveException("в плейлисте нет доступных видео (или он приватный)");
        return list;
    }

    /// <summary>
    /// Поиск без полной загрузки страниц: берём первый результат, который не эфир
    /// и набрал не меньше minViews просмотров (0 — без фильтра).
    /// </summary>
    public async Task<Track> Search(string query, string source, long minViews = 0)
    {
        var count = minViews > 0 ? 10 : 5;
        var json = await Run(new[] { "-j", "--flat-playlist", $"ytsearch{count}:" + query }, TimeSpan.FromSeconds(60));
        var skippedLow = false;
        var candidates = new List<Track>();
        foreach (var line in json.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("{")) continue;
            var info = J.ParseLoose(line);
            if (IsLive(info) || info.Num("duration") <= 0 || info.Str("id") == "") continue;
            var views = (long)info.Num("view_count", -1);
            if (minViews > 0 && views >= 0 && views < minViews) { skippedLow = true; continue; }
            candidates.Add(ToTrack(info, source, query));
        }
        if (candidates.Count == 0)
            throw new ResolveException(skippedLow
                ? $"не найдено видео с {FormatViews(minViews)}+ просмотрами"
                : "ничего не найдено");
        // Быстрый поиск не видит ограничений (18+, блокировка, только для спонсоров) — проверяем
        // найденное полной загрузкой сведений и при ошибке берём следующий результат.
        ResolveException last = null;
        foreach (var c in candidates.Take(3))
        {
            try
            {
                var full = await ByUrl(c.PageUrl, source);
                if (full.Views < 0) full.Views = c.Views;
                return full;
            }
            catch (ResolveException e) when (e.Message != "нет связи с YouTube" && !e.Message.StartsWith("YouTube"))
            {
                last = e;   // это видео недоступно — пробуем следующее
            }
        }
        throw new ResolveException("подходящие видео недоступны: " + last?.Message);
    }

    /// <summary>Скачивает звук (предпочтительно M4A/AAC — его надёжно читает Windows) и возвращает путь к файлу.</summary>
    public async Task<string> DownloadAudio(string videoId, string dir)
    {
        Directory.CreateDirectory(dir);
        string Existing() => Directory.GetFiles(dir, $"yt_{videoId}.*")
            .FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") && new FileInfo(f).Length > 0);
        var ready = Existing();
        if (ready != null) return ready;
        await Run(new[]
        {
            "-f", "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio",
            "--no-playlist", "--no-part", "--no-mtime",
            "-o", Path.Combine(dir, $"yt_{videoId}.%(ext)s"),
            "https://www.youtube.com/watch?v=" + videoId,
        }, TimeSpan.FromMinutes(4));
        return Existing() ?? throw new ResolveException("yt-dlp не сохранил аудио");
    }
}
