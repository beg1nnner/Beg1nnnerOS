using System;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Beg1nnnerOS.Core;
using Beg1nnnerOS.Desktop;
using Beg1nnnerOS.Web;
using WinForms = System.Windows.Forms;

namespace Beg1nnnerOS;

public partial class App : Application
{
    public Hub Hub;
    private WebServer _web;
    private MainWindow _win;
    private WinForms.NotifyIcon _tray;
    private HotkeyManager _hotkeys;
    private Mutex _mutex;
    private bool _trayHintShown;
    private bool _exiting;

    public string BaseUrl => $"http://127.0.0.1:{_web.Port}";
    public string PanelUrl => BaseUrl + "/panel";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Перезапуск (смена профиля Pro, восстановление копии): ждём, пока прежний экземпляр закроется
        var after = Array.IndexOf(e.Args, "--after");
        if (after >= 0 && after + 1 < e.Args.Length && int.TryParse(e.Args[after + 1], out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); } catch { }
        }
        _mutex = new Mutex(true, "Beg1nnnerOS-single-instance", out var created);
        if (!created)
        {
            // Уже запущено (возможно, свёрнуто в трей) — показываем существующее окно
            await SignalExisting();
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (s, ex) =>
        {
            Hub?.Log.Error($"Ошибка интерфейса: {ex.Exception.Message}");
            ex.Handled = true;
        };

        try
        {
            Hub = new Hub();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить Beg1nnnerOS: " + ex.Message, "Beg1nnnerOS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _web = new WebServer(Hub) { ShowWindow = () => Dispatcher.InvokeAsync(ShowMain) };
        try
        {
            await _web.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Порт {Hub.S.Int("server.port", 8080)} или {Config.CallbackPort} занят другой программой.\n\n" +
                            $"Закройте её или смените порт в файле {Path.Combine(Config.Profile, "settings.json")}.\n\n{ex.Message}",
                "Beg1nnnerOS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        Hub.RestartApp = () => Dispatcher.InvokeAsync(async () => await RestartApp());
        Hub.ExitApp = () => Dispatcher.InvokeAsync(async () => await ExitApp());
        Hub.UpdateFound = v => Dispatcher.InvokeAsync(() => ShowUpdate(v));
        Hub.Start();
        _win = new MainWindow(this);
        _hotkeys = new HotkeyManager(_win, name => Hub.Loop.Post(() => RunAction(name)));
        Hub.HotkeyStatus = () => _hotkeys.Status;
        Hub.ApplyHotkeys = () =>
        {
            var cfg = Hub.S["hotkeys"]?.DeepClone() ?? new JsonObject();
            Dispatcher.InvokeAsync(() => _hotkeys.Apply(cfg, Hub.Log));
        };
        Hub.Loop.Post(() => Hub.ApplyHotkeys());
        if (Hub.S.Bool("ui.tray", true)) CreateTray();
        else Hub.TrayStatus = "выключен в настройках";
        _win.Show();
    }

    private static async Task SignalExisting()
    {
        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(Config.Profile, "settings.json")));
            var port = settings.Int("server.port", 8080);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            await http.PostAsync($"http://127.0.0.1:{port}/api/app/show", new StringContent(""));
        }
        catch { }
    }

    /// <summary>Действия трея и горячих клавиш (выполняются в HubLoop).</summary>
    private void RunAction(string name)
    {
        switch (name)
        {
            case "pause_music": Hub.Music.TogglePause(); break;
            case "skip": Hub.Music.Skip("горячая клавиша"); break;
            case "like_track":
                try
                {
                    var (liked, msg) = Hub.Music.ToggleLikeCurrent();
                    ShowBalloon(liked ? "♥ Трек понравился" : "♡ Лайк снят", msg);
                }
                catch (Beg1nnnerOS.Twitch.UserError e) { ShowBalloon("Лайк трека", e.Message); }
                break;
            case "stop_alerts": Hub.Loop.Start(Hub.Alerts.StopAll); break;
            case "pause_events": Hub.Events.SetPaused(!Hub.Events.Paused); break;
        }
    }

    private void CreateTray()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Открыть Beg1nnnerOS", null, (s, e) => ShowMain());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Пауза / продолжить музыку", null, (s, e) => Hub.Loop.Post(() => RunAction("pause_music")));
            menu.Items.Add("Пропустить трек", null, (s, e) => Hub.Loop.Post(() => RunAction("skip")));
            var pauseEvents = new WinForms.ToolStripMenuItem("Пауза уведомлений");
            pauseEvents.Click += (s, e) => Hub.Loop.Post(() => RunAction("pause_events"));
            menu.Items.Add(pauseEvents);
            menu.Items.Add("Остановить МедиаАлерты", null, (s, e) => Hub.Loop.Post(() => RunAction("stop_alerts")));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("💜 Поблагодарить разработчика", null, (s, e) => Shell.OpenUrl("https://www.donationalerts.com/r/beginner24"));
            menu.Items.Add("✉ Связь, предложения и ошибки", null, (s, e) => Shell.OpenUrl("https://t.me/Beg1nnner"));
            menu.Items.Add("Выход", null, async (s, e) => await ExitApp());
            menu.Opening += (s, e) => pauseEvents.Checked = Hub.Events.Paused;
            _tray = new WinForms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "app.ico")),
                Text = "Beg1nnnerOS",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _tray.DoubleClick += (s, e) => ShowMain();
            Hub.TrayStatus = "работает";
        }
        catch (Exception ex)
        {
            Hub.TrayStatus = "ошибка: " + ex.Message;
        }
    }

    public void ShowMain() => _win?.ShowFromTray();

    private WinForms.ToolStripMenuItem _trayUpdate;

    /// <summary>Новая версия: уведомление Windows и пункт «Обновить» в меню значка в трее.</summary>
    private void ShowUpdate(string version)
    {
        if (_tray == null) return;
        try
        {
            if (_trayUpdate == null)
            {
                _trayUpdate = new WinForms.ToolStripMenuItem { Font = new System.Drawing.Font(_tray.ContextMenuStrip.Font, System.Drawing.FontStyle.Bold) };
                _trayUpdate.Click += (s, e) => Hub.Loop.Post(() =>
                {
                    try { Hub.Updater.EnsureCanInstall(); Hub.Loop.Start(Hub.Updater.Install); }
                    catch (Exception ex) { Hub.Log.Error("Обновление: " + ex.Message); Shell.OpenUrl(Hub.Updater.PageUrl); }
                });
                _tray.ContextMenuStrip.Items.Insert(0, _trayUpdate);
                _tray.ContextMenuStrip.Items.Insert(1, new WinForms.ToolStripSeparator());
            }
            _trayUpdate.Text = $"⬇ Обновить до версии {version}";
            _tray.BalloonTipClicked -= OnUpdateBalloon;
            _tray.BalloonTipClicked += OnUpdateBalloon;
            _tray.ShowBalloonTip(8000, $"Доступна новая версия Beg1nnnerOS {version}",
                "Нажмите, чтобы открыть программу, затем «Обновить». Настройки сохранятся.", WinForms.ToolTipIcon.Info);
        }
        catch { }
    }

    /// <summary>Короткое уведомление Windows (например, «трек понравился» по горячей клавише во время игры).</summary>
    private void ShowBalloon(string title, string text)
    {
        Dispatcher.InvokeAsync(() =>
        {
            try { _tray?.ShowBalloonTip(2500, title, text, WinForms.ToolTipIcon.None); } catch { }
        });
    }

    private void OnUpdateBalloon(object sender, EventArgs e)
    {
        _tray.BalloonTipClicked -= OnUpdateBalloon;
        ShowMain();
    }

    /// <summary>Крестик: свернуть в трей, закрыть или спросить (раздел 15 руководства).</summary>
    public async void HandleCloseRequest()
    {
        var action = Hub.S.Str("ui.close_action", "ask");
        var trayAvailable = _tray != null;
        if (action == "ask" || (action == "tray" && !trayAvailable))
        {
            var dlg = new CloseDialog(trayAvailable) { Owner = _win };
            dlg.ShowDialog();
            action = dlg.Choice;
            if (dlg.Remember)
            {
                var remembered = action;
                Hub.Loop.Post(() =>
                {
                    Hub.S.Set("ui.close_action", remembered);
                    Hub.Settings.Save();
                });
            }
        }
        if (action == "tray" && trayAvailable)
        {
            _win.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, "Beg1nnnerOS работает в трее",
                    "Музыка, чат и OBS продолжают работать. Полный выход — через меню значка.", WinForms.ToolTipIcon.Info);
            }
        }
        else if (action == "exit")
            await ExitApp();
    }

    /// <summary>Запускает новый экземпляр (он дождётся закрытия этого) и закрывает текущий.</summary>
    public async Task RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--after {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
        }
        catch (Exception ex)
        {
            Hub?.Log.Error("Не удалось перезапустить программу: " + ex.Message);
            return;
        }
        await ExitApp();
    }

    public async Task ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        if (_win != null) _win.ReallyExit = true;
        try { _hotkeys?.Dispose(); } catch { }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Hub?.Stop();
        try { if (_web != null) await _web.Stop(); } catch { }
        _win?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
