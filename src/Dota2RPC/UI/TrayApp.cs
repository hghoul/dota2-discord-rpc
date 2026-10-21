using Dota2RPC.Core;
using Dota2RPC.Discord;

namespace Dota2RPC.UI;

internal static class AppIcon
{
    static Icon? _icon;

    public static Icon Load()
    {
        if (_icon is not null) return _icon;
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Dota2RPC.app.ico");
        _icon = stream is null ? SystemIcons.Application : new Icon(stream);
        return _icon;
    }

    public static Icon Sized(int size) => new(Load(), new Size(size, size));

    public static Bitmap Bitmap(int size)
    {
        using var icon = Sized(Math.Max(size, 48));
        using var full = icon.ToBitmap();
        var result = new Bitmap(size, size);
        using var g = Graphics.FromImage(result);
        Theme.HighQuality(g);
        g.DrawImage(full, 0, 0, size, size);
        return result;
    }
}

/// <summary>
/// Значок в области уведомлений и жизненный цикл программы.
/// В режиме --watch (запуск «вместе с Dota 2») значок скрыт, пока игра не запущена.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    readonly AppController _app;
    readonly SingleInstance _instance;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _statusItem;
    readonly SynchronizationContext _ui;
    readonly System.Windows.Forms.Timer _trayTimer;
    MainForm? _form;
    bool _watchMode;
    bool _exiting;

    ToolStripMenuItem _updateItem = null!;
    UpdateInfo? _notifiedUpdate;

    public TrayApp(AppController app, SingleInstance instance, bool showWindow, bool watchMode, int initialPage = 0, bool justUpdated = false)
    {
        _app = app;
        _instance = instance;
        _watchMode = watchMode;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = Theme.Font(9.75f) };
        _statusItem = new ToolStripMenuItem("…") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть окно", null, (_, _) => ShowWindow(0));
        menu.Items.Add("Настройки", null, (_, _) => ShowWindow(1));
        menu.Items.Add("Тест RPC", null, async (_, _) => await TestFromTrayAsync());
        _updateItem = new ToolStripMenuItem("Обновить", null, (_, _) => ShowWindow(0)) { Visible = false, Font = Theme.Font(9.75f, FontStyle.Bold) };
        menu.Items.Add(_updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Sized(SystemInformation.SmallIconSize.Width),
            Text = "Dota 2 Discord Rich Presence",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow(0);
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowWindow(0);
        };

        _app.DotaRunningChanged += _ => _ui.Post(_ => UpdateTray(), null);
        _app.StatusChanged += () => _ui.Post(_ => UpdateTray(), null);
        _app.UpdateAvailable += update => _ui.Post(_ => OnUpdateAvailable(update), null);
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_notifiedUpdate is not null) ShowWindow(0);
        };
        _instance.Listen(
            onShow: () => _ui.Post(_ => ShowWindow(0), null),
            onExit: () => _ui.Post(_ => ExitApp(), null));

        _trayTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _trayTimer.Tick += (_, _) => UpdateTray();
        _trayTimer.Start();

        Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding;

        UpdateTray();
        if (showWindow) ShowWindow(initialPage);
        if (justUpdated)
        {
            _tray.Visible = true;
            _tray.ShowBalloonTip(5000, "Dota 2 RPC обновлена", $"Установлена версия {AppInfo.Version}.", ToolTipIcon.Info);
        }
    }

    void OnUpdateAvailable(UpdateInfo update)
    {
        if (_exiting) return;
        _updateItem.Text = $"Обновить до {update.Version}";
        _updateItem.Visible = true;
        if (_notifiedUpdate?.Version == update.Version) return; // напоминаем один раз за запуск
        _notifiedUpdate = update;
        _tray.Visible = true;
        _tray.ShowBalloonTip(8000, $"Доступна версия {update.Version}",
            "Нажмите, чтобы открыть программу и обновиться (займёт меньше минуты).", ToolTipIcon.Info);
        UpdateTray();
    }

    void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        Log.Info("Завершение сеанса Windows — останавливаемся");
        _ui.Send(_ => ExitApp(), null);
    }

    void UpdateTray()
    {
        if (_exiting) return;
        var st = _app.GetStatus();
        var discord = st.Discord.State switch
        {
            DiscordState.Connected => "подключено",
            DiscordState.Connecting => "подключение…",
            DiscordState.Idle => "ожидание Dota 2",
            DiscordState.Unavailable => "не запущен",
            DiscordState.NotConfigured => "нет Application ID",
            _ => "ошибка",
        };
        var dota = st.DotaRunning ? "запущена" : "не запущена";
        _statusItem.Text = $"Discord: {discord} · Dota 2: {dota}";
        var tip = $"Dota 2 RPC\nDiscord: {discord}\nDota 2: {dota}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        _tray.Visible = !_watchMode || st.DotaRunning || _form is { Visible: true };
    }

    void ShowWindow(int page)
    {
        if (_exiting) return;
        _watchMode = false; // пользователь открыл программу — значок больше не прячем
        if (_form is null || _form.IsDisposed)
        {
            _form = new MainForm(_app);
            _form.HiddenToTray += OnHiddenToTray;
        }
        _form.ShowPage(page);
        UpdateTray();
    }

    void OnHiddenToTray()
    {
        UpdateTray();
        var settings = _app.Settings;
        if (settings.TrayHintShown) return;
        _app.UpdateSettings(s => s.TrayHintShown = true);
        _tray.ShowBalloonTip(5000, "Dota 2 RPC работает в фоне",
            "Программа продолжит обновлять статус Discord. Открыть окно или выйти можно через значок в трее.", ToolTipIcon.Info);
    }

    async Task TestFromTrayAsync()
    {
        var result = await _app.TestRpcAsync();
        _tray.Visible = true;
        _tray.ShowBalloonTip(5000, result.Ok ? "Тест RPC: успешно" : "Тест RPC: ошибка",
            result.Ok ? "Проверочный статус виден в вашем профиле Discord 20 секунд." : result.Message,
            result.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Выход из программы");
        Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding;
        _trayTimer.Stop();
        _tray.Visible = false;
        try
        {
            _app.Dispose(); // очищает статус Discord и освобождает порт
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка при остановке", ex);
        }
        if (_form is not null && !_form.IsDisposed)
        {
            _form.HiddenToTray -= OnHiddenToTray;
            _form.Dispose();
        }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayTimer.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
