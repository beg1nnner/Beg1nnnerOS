using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Desktop;
using Microsoft.Web.WebView2.Core;

namespace Beg1nnnerOS;

public partial class MainWindow : Window
{
    private readonly App _app;
    public bool ReallyExit;

    private Action _errorAction;

    public MainWindow(App app)
    {
        InitializeComponent();
        SplashText.Text = L.T(SplashText.Text);
        MinBtn.ToolTip = L.T("Свернуть");
        CloseBtn.ToolTip = L.T("Закрыть");
        System.Windows.Automation.AutomationProperties.SetName(MinBtn, L.T("Свернуть"));
        System.Windows.Automation.AutomationProperties.SetName(CloseBtn, L.T("Закрыть"));
        _app = app;
        string theme = "studio";
        try { theme = app.Hub?.S.Str("ui.theme", "studio") ?? "studio"; } catch { }
        ApplyChromeTheme(theme);
        UpdateMaxState();
        SourceInitialized += (s, e) => ApplyDwm();
        StateChanged += (s, e) => UpdateMaxState();
        Loaded += async (s, e) =>
        {
            if (SystemParameters.ClientAreaAnimation) ((Storyboard)Resources["SplashLoading"]).Begin(this, true);
            await InitWeb();
        };
        Closing += OnClosing;
    }

    // ---------- заголовок окна ----------
    private sealed record ChromePalette(string Bg, string Fg, string Muted, string Accent, string AccentText, bool Light);
    private static readonly Dictionary<string, ChromePalette> Palettes = new()
    {
        ["studio"] = new("#09090C", "#F4F4F7", "#9D9DAE", "#8B5CF6", "#C4B5FD", false),
        ["twitch"] = new("#0F0F14", "#E8E8EF", "#9A9AAB", "#9146FF", "#B38BFF", false),
        ["light"] = new("#F5F4F0", "#1B1B20", "#5A5A66", "#6D28D9", "#5B21B6", true),
        ["midnight"] = new("#0B1020", "#E3E9F7", "#8A97B5", "#3B82F6", "#7CB0FF", false),
        ["neon"] = new("#07070D", "#F2EAFF", "#9C8FBF", "#FF2BD6", "#2DE2FF", false),
        ["graphite"] = new("#121212", "#ECECEC", "#9A9A9A", "#D4D4D4", "#FFFFFF", false),
        ["sunset"] = new("#1A1013", "#FBEEE6", "#C09A92", "#FF7A45", "#FFB38A", false),
        ["forest"] = new("#0D1411", "#E4F1E8", "#8FAB9A", "#22C55E", "#86EFAC", false),
    };
    private bool _light;

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color A(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Цвета заголовка и заставки под тему панели (панель сообщает о смене темы через postMessage).</summary>
    public void ApplyChromeTheme(string theme)
    {
        var p = Palettes.TryGetValue(theme ?? "", out var x) ? x : Palettes["studio"];
        var fg = C(p.Fg);
        Resources["BarBg"] = new SolidColorBrush(C(p.Bg));
        Resources["BarFg"] = new SolidColorBrush(fg);
        Resources["BarMuted"] = new SolidColorBrush(C(p.Muted));
        Resources["BarHover"] = new SolidColorBrush(A(fg, 0x1A));
        Resources["BarPressed"] = new SolidColorBrush(A(fg, 0x2A));
        Resources["BarLine"] = new SolidColorBrush(A(fg, p.Light ? (byte)0x1F : (byte)0x12));
        Resources["Accent"] = new SolidColorBrush(C(p.Accent));
        Resources["AccentSoft"] = new SolidColorBrush(A(C(p.Accent), 0x2A));
        Resources["AccentText"] = new SolidColorBrush(C(p.AccentText));
        Background = new SolidColorBrush(C(p.Bg));
        var bg = C(p.Bg);
        if (Web != null) Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(bg.R, bg.G, bg.B);
        _light = p.Light;
        ApplyDwm();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Тёмная (или светлая) системная рамка и тень окна — под цвет заголовка.</summary>
    private void ApplyDwm()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var dark = _light ? 0 : 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));        // старые сборки Windows 10
        }
        catch { }
    }

    private void UpdateMaxState()
    {
        var max = WindowState == WindowState.Maximized;
        // развёрнутое окно с WindowChrome выходит за край экрана на ширину рамки — компенсируем отступом
        var t = SystemParameters.WindowResizeBorderThickness;
        Frame.Padding = max ? new Thickness(t.Left + 1, t.Top + 1, t.Right + 1, t.Bottom + 1) : new Thickness(0);
        MaxIcon.Data = Geometry.Parse(max ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z" : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
        var tip = L.T(max ? "Восстановить" : "Развернуть");
        MaxBtn.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(MaxBtn, tip);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ---------- заставка ----------
    private void HideSplash()
    {
        if (Splash.Visibility != Visibility.Visible) return;
        if (!SystemParameters.ClientAreaAnimation) { Splash.Visibility = Visibility.Collapsed; return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (s, e) =>
        {
            Splash.Visibility = Visibility.Collapsed;
            ((Storyboard)Resources["SplashLoading"]).Stop(this);
        };
        Splash.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowError(string text, string button, Action action)
    {
        Splash.BeginAnimation(OpacityProperty, null);
        Splash.Opacity = 1;
        Splash.Visibility = Visibility.Visible;
        Loading.Visibility = Visibility.Collapsed;
        SplashText.Visibility = Visibility.Collapsed;
        ErrorText.Text = text;
        ErrorButtonText.Text = button;
        _errorAction = action;
        ErrorBox.Visibility = Visibility.Visible;
        ErrorButton.Focus();
    }

    private void OnErrorButton(object sender, RoutedEventArgs e) => _errorAction?.Invoke();

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
            core.NavigationCompleted += (s, e) => HideSplash();
            core.ProcessFailed += (s, e) => SplashText.Text = L.T("Интерфейс перезапускается…");
            // панель сообщает о смене темы — перекрашиваем заголовок окна
            core.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var msg = JsonNode.Parse(e.WebMessageAsJson);
                    if (msg?["type"]?.GetValue<string>() == "theme") ApplyChromeTheme(msg["theme"]?.GetValue<string>());
                }
                catch { }
            };
            Web.Source = new Uri(_app.PanelUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError(L.T("Для интерфейса нужен компонент Microsoft Edge WebView2 Runtime — он бесплатный и ставится за минуту. Скачайте его со страницы Microsoft и перезапустите Beg1nnnerOS."),
                L.T("Открыть страницу загрузки"), () => Shell.OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/"));
        }
        catch (Exception ex)
        {
            ShowError(L.T("Не удалось открыть интерфейс: ") + ex.Message, L.T("Повторить"), async () =>
            {
                ErrorBox.Visibility = Visibility.Collapsed;
                Loading.Visibility = Visibility.Visible;
                SplashText.Visibility = Visibility.Visible;
                await InitWeb();
            });
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
