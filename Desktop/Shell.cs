using System;
using System.Diagnostics;

namespace Beg1nnnerOS.Desktop;

public static class Shell
{
    /// <summary>Открывает ссылку в браузере по умолчанию (вход в Twitch, инструкции).</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Открывает Проводник с выделенным файлом.</summary>
    public static void ShowFile(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }
}
