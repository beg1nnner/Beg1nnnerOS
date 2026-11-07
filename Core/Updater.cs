using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Twitch;

namespace Beg1nnnerOS.Core;

/// <summary>
/// Проверка обновлений через GitHub Releases.
/// Новая версия = релиз с тегом вида v2.1.1 и приложенным установщиком Beg1nnnerOS-Setup-*.exe.
/// Установка: скачать установщик → проверить SHA-256 (если GitHub его отдал) → закрыть программу →
/// тихо обновить в ту же папку → программа запускается снова (это делает сам установщик).
/// </summary>
public sealed class Updater
{
    /// <summary>Репозиторий с релизами: «владелец/имя».</summary>
    public const string Repo = "beg1nnner/Beg1nnnerOS";

    private static readonly TimeSpan Period = TimeSpan.FromHours(6);
    private readonly Hub _hub;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // отдельный клиент без общего 30-секундного таймаута: установщик весит десятки мегабайт
    private static readonly HttpClient Download = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var c = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Beg1nnnerOS/{Config.Version}");
        return c;
    }

    public string Latest { get; private set; } = "";
    public string Notes { get; private set; } = "";
    public string PageUrl { get; private set; } = "";
    private string _assetUrl = "", _assetName = "", _sha256 = "";
    private long _assetSize;

    public string Status { get; private set; } = "ещё не проверялось";
    public DateTime CheckedAt { get; private set; }
    public bool Downloading { get; private set; }
    public int Progress { get; private set; }   // 0–100 во время скачивания

    public Updater(Hub hub) => _hub = hub;

    public bool Available => Latest != "" && IsNewer(Latest, Config.Version);

    public object State() => new
    {
        current = Config.Version,
        latest = Latest,
        available = Available,
        skipped = Available && _hub.S.Str("update.skip") == Latest,
        notes = Notes,
        page = PageUrl,
        installer = _assetUrl != "",
        size_mb = Math.Round(_assetSize / 1048576.0, 1),
        status = Status,
        checked_at = CheckedAt == default ? "" : CheckedAt.ToLocalTime().ToString("dd.MM HH:mm"),
        downloading = Downloading,
        progress = Progress,
        auto = _hub.S.Bool("update.auto_check", true),
    };

    /// <summary>Фоновый цикл: первая проверка через 15 с после запуска, затем раз в 6 часов.</summary>
    public async Task Run()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        while (true)
        {
            if (_hub.S.Bool("update.auto_check", true))
            {
                try { await Check(); } catch { /* статус уже записан */ }
            }
            await Task.Delay(Period);
        }
    }

    public async Task Check()
    {
        await _gate.WaitAsync();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            HttpResponseMessage resp;
            try { resp = await _hub.Http.SendAsync(req, cts.Token); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Status = "нет связи с GitHub";
                throw;
            }
            using (resp)
            {
                if ((int)resp.StatusCode == 404) { Status = "релизов пока нет"; return; }
                if ((int)resp.StatusCode is 403 or 429) { Status = "GitHub временно ограничил запросы, проверю позже"; return; }
                if (!resp.IsSuccessStatusCode) { Status = $"GitHub ответил ошибкой {(int)resp.StatusCode}"; return; }
                var j = J.ParseLoose(await resp.Content.ReadAsStringAsync());
                var tag = j.Str("tag_name").Trim().TrimStart('v', 'V');
                if (!System.Version.TryParse(Norm(tag), out _)) { Status = $"непонятный номер версии на GitHub: {tag}"; return; }
                var wasNew = !Available || Latest != tag;
                Latest = tag;
                Notes = j.Str("body").Trim();
                PageUrl = j.Str("html_url");
                _assetUrl = _assetName = _sha256 = ""; _assetSize = 0;
                foreach (var a in j.Arr("assets").OfType<JsonNode>())
                {
                    var name = a.Str("name");
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (_assetUrl != "" && !name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) continue;
                    _assetName = name;
                    _assetUrl = a.Str("browser_download_url");
                    _assetSize = (long)a.Num("size");
                    var digest = a.Str("digest");   // «sha256:…», GitHub считает сам
                    _sha256 = digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : "";
                }
                CheckedAt = DateTime.UtcNow;
                if (Available)
                {
                    Status = $"доступна версия {Latest}";
                    if (wasNew) _hub.Log.Add("system", $"Доступно обновление Beg1nnnerOS {Latest} (у вас {Config.Version}). Обновить можно в панели.");
                }
                else Status = "установлена последняя версия";
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Проверки перед установкой — ошибки сразу показываются в панели.</summary>
    public void EnsureCanInstall()
    {
        if (!Available) throw new UserError("Обновлений нет.");
        if (_assetUrl == "") throw new UserError("К релизу не приложен установщик — скачайте обновление со страницы релиза.");
    }

    /// <summary>Скачивает установщик и запускает тихое обновление. Программа после этого закрывается.</summary>
    public async Task Install()
    {
        EnsureCanInstall();
        if (Downloading) return;
        Downloading = true; Progress = 0;
        var path = Path.Combine(Path.GetTempPath(), "Beg1nnnerOS-update", _assetName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Status = "скачиваю обновление…";
            using (var req = new HttpRequestMessage(HttpMethod.Get, _assetUrl))
            {
                using var stall = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                using var resp = await Download.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? _assetSize;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(path);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    sha.AppendData(buf, 0, n);
                    done += n;
                    if (total > 0) Progress = (int)(done * 100 / total);
                }
                if (_assetSize > 0 && done != _assetSize) throw new IOException("файл скачался не полностью");
                var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (_sha256 != "" && hash != _sha256) throw new IOException("контрольная сумма не совпала — файл повреждён");
            }
            Progress = 100;
            Status = "устанавливаю обновление…";
            _hub.Log.Add("system", $"Устанавливаю обновление {Latest}. Программа перезапустится сама.");

            // Ждём ~3 с, чтобы эта копия программы успела закрыться, затем тихая установка поверх.
            // Установщик сам запускает программу после тихой установки.
            var args = $"/c ping -n 4 127.0.0.1 >nul & start \"\" \"{path}\" /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            _hub.ExitApp();
        }
        catch (Exception e)
        {
            var why = e is OperationCanceledException ? "скачивание слишком долгое, проверьте интернет" : e.Message;
            Status = "не удалось обновить: " + why;
            _hub.Log.Error("Обновление: " + why);
            try { File.Delete(path); } catch { }
        }
        finally { Downloading = false; }
    }

    /// <summary>«2.1» → «2.1.0», «2.1.1-beta» → «2.1.1»: чтобы 2.1 и 2.1.0 считались одной версией.</summary>
    private static string Norm(string v)
    {
        var core = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0) return "";
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts.Take(4));
    }

    public static bool IsNewer(string latest, string current) =>
        System.Version.TryParse(Norm(latest), out var a) && System.Version.TryParse(Norm(current), out var b) && a > b;
}
