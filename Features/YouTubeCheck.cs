using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Beg1nnnerOS.Core;

namespace Beg1nnnerOS.Features;

/// <summary>
/// «Проверить связь с YouTube»: по шагам находит, где именно рвётся связь, и советует, что делать.
/// 1) DNS — узнаём адрес youtube.com; 2) HTTPS-соединение; 3) yt-dlp получает сведения о видео.
/// </summary>
public static class YouTubeCheck
{
    public sealed record Step(string Name, bool Ok, string Detail, string Advice);

    private const string TestVideo = "https://www.youtube.com/watch?v=jNQXAC9IVRw";   // первое видео YouTube, доступно всегда

    private const string DnsAdvice =
        "Провайдер не отдаёт адрес YouTube. Включите зашифрованный DNS: «Параметры → Сеть и Интернет → ваше подключение → " +
        "Назначение DNS-сервера → Изменить → Вручную», DNS 1.1.1.1 и 1.0.0.1, «DNS через HTTPS: Вкл.». " +
        "Затем выполните ipconfig /flushdns и проверьте снова. Либо включите VPN для всей системы.";

    private const string ConnAdvice =
        "Адрес YouTube известен, но соединение обрывается — так бывает при замедлении YouTube провайдером. " +
        "Включите VPN для всей системы (или добавьте Beg1nnnerOS.exe и yt-dlp.exe в его список программ). " +
        "Если используете zapret — попробуйте другую стратегию (service.bat → Run Tests). " +
        "Если стоит антивирус с проверкой HTTPS (Касперский, ESET, Avast) — добавьте программу в исключения.";

    public static async Task<object> Run(Hub hub)
    {
        var steps = new List<Step>();
        var sw = Stopwatch.StartNew();

        // 1. DNS
        IPAddress[] ips = Array.Empty<IPAddress>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            ips = await Dns.GetHostAddressesAsync("www.youtube.com", cts.Token);
            var bad = ips.Where(IsFake).ToList();
            if (ips.Length == 0)
                steps.Add(new Step("Адрес youtube.com (DNS)", false, "адрес не найден", DnsAdvice));
            else if (bad.Count == ips.Length)
                steps.Add(new Step("Адрес youtube.com (DNS)", false, $"провайдер подменяет адрес ({bad[0]})", DnsAdvice));
            else
                steps.Add(new Step("Адрес youtube.com (DNS)", true, string.Join(", ", ips.Take(2).Select(i => i.ToString())), ""));
        }
        catch (Exception e)
        {
            steps.Add(new Step("Адрес youtube.com (DNS)", false, ShortNet(e), DnsAdvice));
        }

        // 2. HTTPS
        if (steps[^1].Ok)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var resp = await hub.Http.GetAsync("https://www.youtube.com/generate_204", cts.Token);
                var code = (int)resp.StatusCode;
                steps.Add(code is 204 or 200
                    ? new Step("Соединение с YouTube (HTTPS)", true, $"ответ за {sw.ElapsedMilliseconds} мс", "")
                    : new Step("Соединение с YouTube (HTTPS)", false, $"YouTube ответил кодом {code}", ConnAdvice));
            }
            catch (Exception e)
            {
                steps.Add(new Step("Соединение с YouTube (HTTPS)", false, ShortNet(e), ConnAdvice));
            }
        }

        // 3. yt-dlp
        if (steps[^1].Ok)
        {
            sw.Restart();
            try
            {
                await hub.YtDlp.EnsureExe();
                var t = await hub.YtDlp.ByUrl(TestVideo, "check");
                steps.Add(new Step("Загрузчик yt-dlp", true, $"сведения о видео получены за {sw.Elapsed.TotalSeconds:0.0} с", ""));
            }
            catch (Exception e)
            {
                var why = e is ResolveException ? e.Message : YtDlp.Explain(e.Message);
                var advice = why.Contains("обрывается") || why.Contains("нет связи") ? ConnAdvice
                    : why.Contains("бота") ? "YouTube временно ограничил запросы с вашего адреса. Подождите 10–30 минут или смените VPN-сервер."
                    : why.Contains("изменил") ? "Обновите yt-dlp: перезапустите программу — она скачает свежую версию сама."
                    : "Попробуйте ещё раз через минуту. Если повторяется — сохраните отчёт об ошибке и пришлите разработчику.";
                steps.Add(new Step("Загрузчик yt-dlp", false, why, advice));
            }
        }

        var ok = steps.All(s => s.Ok) && steps.Count == 3;
        var summary = ok ? "YouTube работает: заказы музыки будут проходить." :
            "YouTube недоступен для программы — заказы с YouTube не пройдут. " +
            (hub.S.Str("yandex.token") != "" ? "Яндекс Музыка подключена: заказы по названию можно искать сначала в ней." :
                                               "Как временное решение подключите Яндекс Музыку и выберите «Где искать сначала: Яндекс Музыка».");
        hub.Log.Add(ok ? "system" : "skip", "Проверка YouTube: " + (ok ? "всё работает" : steps.First(s => !s.Ok).Name + " — " + steps.First(s => !s.Ok).Detail));
        return new { ok, summary, steps = steps.Select(s => new { name = s.Name, ok = s.Ok, detail = s.Detail, advice = s.Advice }) };
    }

    /// <summary>Адреса-заглушки, которые подставляют провайдеры при блокировке.</summary>
    private static bool IsFake(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 0;
    }

    private static string ShortNet(Exception e)
    {
        var inner = e;
        while (inner.InnerException != null) inner = inner.InnerException;
        var m = inner.Message;
        if (e is OperationCanceledException || inner is TimeoutException) return "нет ответа (превышено время ожидания)";
        if (inner is SocketException se)
            return se.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "адрес не найден",
                SocketError.ConnectionReset => "соединение сброшено",
                SocketError.ConnectionRefused => "соединение отклонено",
                SocketError.TimedOut => "нет ответа",
                _ => "ошибка сети: " + se.SocketErrorCode,
            };
        if (m.Contains("SSL", StringComparison.OrdinalIgnoreCase) || m.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("handshake", StringComparison.OrdinalIgnoreCase))
            return "защищённое соединение оборвано";
        return m.Length > 120 ? m[..120] : m;
    }
}
