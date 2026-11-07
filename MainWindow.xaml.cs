using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Desktop;
using Microsoft.Web.WebView2.Core;

namespace Beg1nnnerOS;

public partial class MainWindow : Window
{
    private readonly App _app;
    public bool ReallyExit;

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 15, 20);
        Loaded += async (s, e) => await InitWeb();
        Closing += OnClosing;
    }

    private async Task InitWeb()
    {
        try
        {
            // Данные WebView2 — в профиле пользователя (папка программы может быть недоступна для записи)
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Config.Root, "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            // Внешние ссылки и вход в Twitch — в системном браузере
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                Shell.OpenUrl(e.Uri);
            };
            core.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.StartsWith(_app.BaseUrl, StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("about:"))
                {
                    e.Cancel = true;
                    Shell.OpenUrl(e.Uri);
                }
            };
            core.NavigationCompleted += (s, e) => Splash.Visibility = Visibility.Collapsed;
            core.ProcessFailed += (s, e) => SplashText.Text = "Интерфейс перезапускается…";
            Web.Source = new Uri(_app.PanelUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            SplashText.Text = "Не найден Microsoft Edge WebView2 Runtime. Установите его со страницы Microsoft " +
                              "(она откроется в браузере) и перезапустите Beg1nnnerOS.";
            Shell.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
        }
        catch (Exception ex)
        {
            SplashText.Text = "Не удалось открыть интерфейс: " + ex.Message;
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        if (ReallyExit) return;
        e.Cancel = true;
        _app.HandleCloseRequest();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
