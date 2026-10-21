using System.Text.Json;
using Dota2RPC.Core;
using Dota2RPC.Discord;
using Dota2RPC.Gsi;
using Dota2RPC.Steam;

namespace Dota2RPC.UI;

internal sealed class MainForm : Form
{
    readonly AppController _app;
    readonly Panel _content;
    readonly Label _pageTitle;
    readonly Label _pageSubtitle;
    readonly List<(NavButton Button, Control Page, string Title, string Subtitle)> _pages = [];
    readonly System.Windows.Forms.Timer _refresh;
    readonly Label _sidebarStatus;
    readonly Panel _sidebarDot;

    // «Статус»
    StatusItem _stDiscord = null!, _stDota = null!, _stGsi = null!, _stConfig = null!, _stLaunch = null!, _stAutostart = null!;
    PresencePreview _preview = null!;
    FlatButton _testButton = null!;
    Label _testResult = null!;
    readonly Dictionary<string, Label> _matchValues = [];

    // Элементы, которые видны только в режиме разработчика.
    readonly List<Control> _devOnly = [];
    ToggleSwitch _tDev = null!, _tRecord = null!;

    // Обновления
    Card _updateBanner = null!;
    Label _updateBannerText = null!;
    FlatButton _updateNow = null!;
    ToggleSwitch _tUpdates = null!;
    Label _updateStatus = null!;
    bool _updating;

    // «Настройки»
    TextBox _clientId = null!;
    Label _clientIdInfo = null!;
    ToggleSwitch _tWindows = null!, _tDota = null!, _tLevel = null!, _tKda = null!, _tTime = null!, _tScore = null!, _tNet = null!, _tMenu = null!, _tButton = null!, _tEmoji = null!, _tAnim = null!;

    // «Интеграция»
    Label _dotaPathLabel = null!, _gsiStateLabel = null!, _gsiPathLabel = null!, _launchLabel = null!, _diagLabel = null!;
    NumericUpDown _port = null!;
    FlatButton _launchButton = null!;

    GsiConfigStatus? _gsiStatus;
    List<LaunchOptionsInfo>? _launchInfos;
    AutostartStatus? _autostart;
    DateTime _lastIntegrationCheck = DateTime.MinValue;
    bool _integrationCheckRunning;
    string? _discordAppName;
    string? _checkedClientId;

    public MainForm(AppController app)
    {
        _app = app;
        Text = AppInfo.ProductName;
        Icon = AppIcon.Load();
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Font(9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1020, 680);
        MinimumSize = new Size(860, 560);

        // Боковая панель
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 230, BackColor = Theme.Sidebar, Padding = new Padding(0, 0, 0, 12) };
        var brand = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Sidebar };
        var logo = new PictureBox
        {
            Image = AppIcon.Bitmap(44),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(44, 44),
            Location = new Point(20, 24),
            BackColor = Theme.Sidebar,
        };
        brand.Controls.Add(logo);
        brand.Controls.Add(new Label { Text = "Dota 2 RPC", Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = new Point(74, 24), BackColor = Theme.Sidebar });
        brand.Controls.Add(new Label { Text = "Rich Presence · v" + AppInfo.Version, Font = Theme.Font(8.5f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(76, 50), BackColor = Theme.Sidebar });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Theme.Sidebar, Padding = new Padding(22, 8, 12, 8) };
        _sidebarDot = new Panel { Size = new Size(10, 10), Location = new Point(24, 22), BackColor = Theme.Gray };
        _sidebarDot.Paint += (_, e) =>
        {
            Theme.HighQuality(e.Graphics);
            e.Graphics.Clear(Theme.Sidebar);
            using var b = new SolidBrush(_sidebarDot.Tag is Color c ? c : Theme.Gray);
            e.Graphics.FillEllipse(b, 0, 0, _sidebarDot.Width - 1, _sidebarDot.Height - 1);
        };
        _sidebarStatus = new Label { AutoSize = false, Location = new Point(42, 12), Size = new Size(176, 34), ForeColor = Theme.Muted, Font = Theme.Font(8.75f), BackColor = Theme.Sidebar };
        footer.Controls.Add(_sidebarDot);
        footer.Controls.Add(_sidebarStatus);

        var nav = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Padding = new Padding(0, 8, 0, 0) };
        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(footer);
        sidebar.Controls.Add(brand);

        // Область содержимого
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(28, 22, 28, 0) };
        var header = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Theme.Background };
        _pageTitle = new Label { Font = Theme.Font(18f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = true, Location = new Point(0, 0) };
        _pageSubtitle = new Label { Font = Theme.Font(9.5f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(2, 40) };
        header.Controls.Add(_pageTitle);
        header.Controls.Add(_pageSubtitle);
        _content = new ScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background, Padding = new Padding(0, 0, 8, 16) };
        main.Controls.Add(_content);
        main.Controls.Add(header);

        Controls.Add(main);
        Controls.Add(sidebar);

        AddPage(nav, "Статус", "", "Статус", "Подключения, текущий матч и предпросмотр статуса в Discord", BuildStatusPage());
        AddPage(nav, "Настройки", "", "Настройки", "Автозапуск и оформление статуса", BuildSettingsPage());
        AddPage(nav, "Интеграция", "", "Интеграция с Dota 2", "Подключение программы к игре", BuildIntegrationPage());
        // Порядок Dock=Top обратный: переставляем, чтобы «Статус» был сверху.
        for (var i = 0; i < _pages.Count; i++) _pages[i].Button.BringToFront();
        SelectPage(0);

        LoadSettingsIntoControls();

        _refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();
        _app.StatusChanged += OnAppStatusChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyDarkTitleBar(this);
        Theme.ApplyDarkScrollBars(_content);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = null;
        ScrollToTop();
        RefreshStatus();
        _ = CheckDiscordAppAsync();
        // Первый запуск без Application ID — сразу показываем, где его указать.
        if (!AppSettings.IsValidClientId(_app.Settings.EffectiveClientId) && _pages[0].Button.Selected) SelectPage(1);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // Скрытое в трей окно ничего не обновляет: таймер и проверки файлов Steam работают только при показе.
        _refresh.Enabled = Visible;
        if (Visible)
        {
            _lastIntegrationCheck = DateTime.MinValue;
            RefreshStatus();
        }
    }

    void OnAppStatusChanged()
    {
        if (IsHandleCreated && !IsDisposed && Visible)
            BeginInvoke(RefreshStatus);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.StatusChanged -= OnAppStatusChanged;
            _refresh.Dispose();
        }
        base.Dispose(disposing);
    }

    public void ShowPage(int index)
    {
        SelectPage(index);
        BringToForeground();
    }

    public void BringToForeground()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void AddPage(Panel nav, string text, string glyph, string title, string subtitle, Control page)
    {
        var button = new NavButton(text, glyph);
        var index = _pages.Count;
        button.Click += (_, _) => SelectPage(index);
        nav.Controls.Add(button);
        page.Visible = false;
        _content.Controls.Add(page);
        _pages.Add((button, page, title, subtitle));
    }

    void SelectPage(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Button.Selected = i == index;
            _pages[i].Page.Visible = i == index;
        }
        _pageTitle.Text = _pages[index].Title;
        _pageSubtitle.Text = _pages[index].Subtitle;
        ScrollToTop();
    }

    void ScrollToTop()
    {
        _content.AutoScrollPosition = Point.Empty;
        // После раскладки страницы (и переключения режима разработчика) панель может «уехать» вниз —
        // повторяем сброс, когда раскладка уже завершена.
        if (IsHandleCreated) BeginInvoke(() => _content.AutoScrollPosition = Point.Empty);
    }

    T Dev<T>(T control) where T : Control
    {
        _devOnly.Add(control);
        return control;
    }

    void ApplyDeveloperMode(bool on)
    {
        SuspendLayout();
        foreach (var c in _devOnly) c.Visible = on;
        ResumeLayout(true);
    }

    static TableLayoutPanel Column(params Control[] cards)
    {
        var col = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Background,
            Margin = Padding.Empty,
        };
        col.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var c in cards)
        {
            c.Dock = DockStyle.Fill;
            col.Controls.Add(c);
        }
        return col;
    }

    static TableLayoutPanel PageLayout(int columns)
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columns,
            BackColor = Theme.Background,
        };
        for (var i = 0; i < columns; i++) page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        return page;
    }

    // ───────────────────────────── Страница «Статус» ─────────────────────────────

    Control BuildStatusPage()
    {
        var page = PageLayout(2);

        var connections = new Card("Состояние подключения");
        _stDiscord = connections.Add(new StatusItem("Discord"));
        _stDota = connections.Add(new StatusItem("Dota 2"));
        _stGsi = connections.Add(new StatusItem("Данные игры (GSI)"));
        _stConfig = Dev(connections.Add(new StatusItem("Конфигурация GSI")));
        _stLaunch = Dev(connections.Add(new StatusItem("Параметр запуска -gamestateintegration")));
        _stAutostart = connections.Add(new StatusItem("Автозапуск"));

        var match = new Card("Текущий матч");
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            BackColor = Theme.Card,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var key in new[] { "Герой", "Уровень", "K / D / A", "Время матча", "Этап", "Счёт", "Последний матч" })
        {
            grid.Controls.Add(new Label { Text = key, ForeColor = Theme.Muted, AutoSize = true, Font = Theme.Font(9.5f), Margin = new Padding(0, 3, 18, 3) });
            var value = new Label { Text = "—", ForeColor = Theme.Text, AutoSize = true, Font = Theme.Font(9.75f, FontStyle.Bold), Margin = new Padding(0, 3, 0, 3) };
            grid.Controls.Add(value);
            _matchValues[key] = value;
        }
        match.Add(grid);

        var preview = new Card("Предпросмотр в Discord", "Так ваш статус увидят друзья. Данные обновляются автоматически.");
        _preview = preview.Add(new PresencePreview());
        _testButton = new FlatButton("Тест RPC", ButtonKind.Discord);
        _testButton.Click += async (_, _) => await RunRpcTestAsync();
        var portal = Dev(new FlatButton("Discord Developer Portal"));
        portal.Click += (_, _) => Ui.OpenUrl(AppInfo.DeveloperPortalUrl);
        preview.Add(Ui.ButtonRow(_testButton, portal));
        _testResult = preview.Add(Ui.Muted("«Тест RPC» на 20 секунд показывает проверочный статус — даже если Dota 2 не запущена."));

        _updateBanner = new Card("Доступна новая версия");
        _updateBannerText = _updateBanner.Add(Ui.Value(""));
        _updateNow = new FlatButton("Обновить сейчас", ButtonKind.Primary);
        _updateNow.Click += async (_, _) => await InstallUpdateAsync();
        var notes = new FlatButton("Что нового");
        notes.Click += (_, _) =>
        {
            if (_app.AvailableUpdate is { } u) Ui.OpenUrl(u.PageUrl);
        };
        var skip = new FlatButton("Пропустить эту версию");
        skip.Click += (_, _) =>
        {
            if (_app.AvailableUpdate is { } u) ApplySetting(s => s.SkippedUpdateVersion = u.Version.ToString());
            RefreshUpdateUi();
        };
        _updateBanner.Add(Ui.ButtonRow(_updateNow, notes, skip));
        _updateBanner.Visible = false;
        _updateBanner.Dock = DockStyle.Fill; // на всю ширину страницы, над обеими колонками
        page.Controls.Add(_updateBanner, 0, 0);
        page.SetColumnSpan(_updateBanner, 2);

        var left = Column(connections);
        var right = Column(preview, match);
        left.Margin = new Padding(0, 0, 10, 0);
        right.Margin = new Padding(10, 0, 0, 0);
        page.Controls.Add(left, 0, 1);
        page.Controls.Add(right, 1, 1);
        return page;
    }

    async Task RunRpcTestAsync()
    {
        _testButton.Enabled = false;
        _testResult.ForeColor = Theme.Muted;
        _testResult.Text = "Отправка тестового статуса в Discord…";
        try
        {
            var result = await _app.TestRpcAsync();
            _testResult.ForeColor = result.Ok ? Theme.Green : Theme.Red;
            _testResult.Text = result.Ok
                ? "✔ " + result.Message + ". Откройте свой профиль в Discord — статус будет виден 20 секунд."
                : "✖ " + result.Message;
        }
        catch (Exception ex)
        {
            _testResult.ForeColor = Theme.Red;
            _testResult.Text = "✖ " + ex.Message;
        }
        finally
        {
            _testButton.Enabled = true;
        }
    }

    // ───────────────────────────── Страница «Настройки» ─────────────────────────────

    Control BuildSettingsPage()
    {
        var page = PageLayout(1);

        var discord = new Card("Приложение Discord", AppInfo.EmbeddedClientId.Length > 0
            ? "Программа уже содержит Application ID — ничего настраивать не нужно. " +
              "Поле ниже нужно, только если вы хотите показывать статус от имени своего приложения из Discord Developer Portal; оставьте его пустым, чтобы использовать встроенный ID."
            : "Discord показывает Rich Presence от имени зарегистрированного приложения. Создайте его в Discord Developer Portal " +
              "(New Application → имя «Dota 2»), скопируйте Application ID на вкладке General Information и вставьте сюда.");
        var idRow = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, BackColor = Theme.Card, Margin = new Padding(0, 4, 0, 4) };
        idRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        idRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        idRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _clientId = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            Font = Theme.Font(11f),
            Dock = DockStyle.Fill,
            MaxLength = 21,
            PlaceholderText = AppInfo.EmbeddedClientId.Length > 0 ? $"Встроенный: {AppInfo.EmbeddedClientId}" : "Application ID (только цифры)",
            Margin = new Padding(0, 2, 10, 0),
        };
        _clientId.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                SaveClientId();
            }
        };
        var save = new FlatButton("Сохранить", ButtonKind.Primary);
        save.Click += (_, _) => SaveClientId();
        var portal = new FlatButton("Открыть портал");
        portal.Click += (_, _) => Ui.OpenUrl(AppInfo.DeveloperPortalUrl);
        idRow.Controls.Add(_clientId, 0, 0);
        idRow.Controls.Add(save, 1, 0);
        idRow.Controls.Add(portal, 2, 0);
        discord.Add(idRow);
        _clientIdInfo = discord.Add(Ui.Muted(""));
        if (AppInfo.EmbeddedClientId.Length > 0) Dev(discord); // без встроенного ID поле нужно всем

        var startup = new Card("Запуск");
        _tWindows = new ToggleSwitch();
        _tDota = new ToggleSwitch();
        startup.Add(Ui.ToggleRow("Запускать вместе с Windows",
            "Программа стартует при входе в систему и работает в области уведомлений. Discord подключается только когда запущена Dota 2.", _tWindows));
        startup.Add(Ui.ToggleRow("Запускать вместе с Dota 2",
            "Программа незаметно ожидает запуска dota2.exe и включается вместе с игрой: значок в трее появится, когда игра запустится.", _tDota));

        var display = new Card("Что показывать в статусе", "Отображаются только данные, которые реально передаёт игра через GSI.");
        _tLevel = new ToggleSwitch();
        _tKda = new ToggleSwitch();
        _tTime = new ToggleSwitch();
        _tScore = new ToggleSwitch();
        _tNet = new ToggleSwitch();
        _tMenu = new ToggleSwitch();
        _tButton = new ToggleSwitch();
        _tEmoji = new ToggleSwitch();
        _tAnim = new ToggleSwitch();
        var animRow = display.Add(Ui.ToggleRow("Анимированная иконка героя",
            "Вместо картинки — живой рендер героя (анимация с сайта Dota 2). Если анимации для героя нет, будет обычная иконка.", _tAnim));
        animRow.Visible = HeroAnimations.Configured;
        display.Add(Ui.ToggleRow("Цветное оформление", "Эмодзи в статусе: 🏠 меню, ⭐ уровень, 🗡️ убийства · 💀 смерти · 🤝 помощи, ⏱️ время, 🔥 серия, 🏆 победа.", _tEmoji));
        display.Add(Ui.ToggleRow("Уровень героя", "Например: «Pudge · Ур. 14».", _tLevel));
        display.Add(Ui.ToggleRow("Убийства / смерти / помощи", "Например: «KDA 7 / 2 / 11».", _tKda));
        display.Add(Ui.ToggleRow("Игровое время", "Минуты матча в тексте и таймер «прошло мм:сс», который Discord обновляет сам.", _tTime));
        display.Add(Ui.ToggleRow("Сторона и счёт", "В подсказке к иконке героя: «Силы Света · Счёт 21 : 14».", _tScore));
        display.Add(Ui.ToggleRow("LH/DN, GPM, XPM", "Расширенная статистика в подсказке к иконке героя.", _tNet));
        display.Add(Ui.ToggleRow("Статус в главном меню", "Показывать «В главном меню», когда вы не в матче.", _tMenu));
        display.Add(Ui.ToggleRow("Кнопка «Профиль на OpenDota»", "Кнопка в статусе со ссылкой на ваш профиль (видна другим пользователям, не вам).", _tButton));

        foreach (var (toggle, apply) in new (ToggleSwitch, Action<AppSettings, bool>)[]
                 {
                     (_tWindows, (s, v) => s.StartWithWindows = v),
                     (_tDota, (s, v) => s.StartWithDota = v),
                     (_tLevel, (s, v) => s.ShowLevel = v),
                     (_tKda, (s, v) => s.ShowKda = v),
                     (_tTime, (s, v) => s.ShowGameTime = v),
                     (_tScore, (s, v) => s.ShowTeamAndScore = v),
                     (_tNet, (s, v) => s.ShowNetStats = v),
                     (_tMenu, (s, v) => s.ShowInMenu = v),
                     (_tButton, (s, v) => s.ShowProfileButton = v),
                     (_tEmoji, (s, v) => s.ShowEmoji = v),
                     (_tAnim, (s, v) => s.AnimatedHeroIcons = v),
                 })
        {
            var t = toggle;
            t.CheckedChanged += (_, _) => ApplySetting(s => apply(s, t.Checked));
        }

        var developer = new Card("Для разработчиков");
        _tDev = new ToggleSwitch();
        developer.Add(Ui.ToggleRow("Режим разработчика",
            "Показывает технические настройки: Application ID Discord, порт и файл конфигурации GSI, " +
            "параметры запуска по профилям Steam, журнал и диагностику.", _tDev));
        _tDev.CheckedChanged += (_, _) =>
        {
            ApplySetting(s => s.DeveloperMode = _tDev.Checked);
            ApplyDeveloperMode(_tDev.Checked);
        };

        var updates = new Card("Обновления");
        _tUpdates = new ToggleSwitch();
        updates.Add(Ui.ToggleRow("Проверять обновления",
            "Раз в 12 часов программа смотрит последний релиз на GitHub и напоминает о новой версии. Обновление ставится только по вашей кнопке.", _tUpdates));
        _tUpdates.CheckedChanged += (_, _) => ApplySetting(s => s.CheckUpdates = _tUpdates.Checked);
        _updateStatus = updates.Add(Ui.Muted($"Установлена версия {AppInfo.Version}."));
        var checkNow = new FlatButton("Проверить сейчас");
        checkNow.Click += async (_, _) => await CheckUpdatesNowAsync(checkNow);
        var releases = new FlatButton("Все версии на GitHub");
        releases.Click += (_, _) => Ui.OpenUrl($"https://github.com/{UpdateChecker.Repository}/releases");
        updates.Add(Ui.ButtonRow(checkNow, releases));

        page.Controls.Add(Column(discord, startup, display, updates, developer), 0, 0);
        return page;
    }

    void LoadSettingsIntoControls()
    {
        var s = _app.Settings;
        _clientId.Text = s.DiscordClientId;
        _tWindows.SetSilently(s.StartWithWindows);
        _tDota.SetSilently(s.StartWithDota);
        _tLevel.SetSilently(s.ShowLevel);
        _tKda.SetSilently(s.ShowKda);
        _tTime.SetSilently(s.ShowGameTime);
        _tScore.SetSilently(s.ShowTeamAndScore);
        _tNet.SetSilently(s.ShowNetStats);
        _tMenu.SetSilently(s.ShowInMenu);
        _tButton.SetSilently(s.ShowProfileButton);
        _tEmoji.SetSilently(s.ShowEmoji);
        _tAnim.SetSilently(s.AnimatedHeroIcons);
        _tDev.SetSilently(s.DeveloperMode);
        _tUpdates.SetSilently(s.CheckUpdates);
        _tRecord.SetSilently(s.RecordGsiSamples);
        ApplyDeveloperMode(s.DeveloperMode);
        _port.Value = Math.Clamp(s.GsiPort, (int)_port.Minimum, (int)_port.Maximum);
    }

    void ApplySetting(Action<AppSettings> mutate)
    {
        try
        {
            _app.UpdateSettings(mutate);
            _lastIntegrationCheck = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось применить настройку", ex);
            MessageBox.Show(this, "Не удалось сохранить настройку:\n" + ex.Message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            LoadSettingsIntoControls();
        }
    }

    void SaveClientId()
    {
        var id = new string(_clientId.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        _clientId.Text = id;
        if (id.Length > 0 && !AppSettings.IsValidClientId(id))
        {
            _clientIdInfo.ForeColor = Theme.Red;
            _clientIdInfo.Text = "✖ Application ID состоит только из цифр (обычно 18–19). Скопируйте его на вкладке General Information.";
            return;
        }
        ApplySetting(s => s.DiscordClientId = id);
        _ = CheckDiscordAppAsync();
    }

    /// <summary>Проверяет ID через публичный эндпоинт Discord /applications/{id}/rpc и узнаёт имя приложения.</summary>
    async Task CheckDiscordAppAsync()
    {
        var settings = _app.Settings;
        var id = settings.EffectiveClientId;
        var source = settings.UsesEmbeddedClientId ? "встроенный ID" : "ваш ID";
        if (!AppSettings.IsValidClientId(id))
        {
            _clientIdInfo.ForeColor = Theme.Yellow;
            _clientIdInfo.Text = "Application ID не указан — статус в Discord не будет отображаться.";
            _discordAppName = null;
            return;
        }
        if (_checkedClientId == id && _discordAppName is not null) return;

        _clientIdInfo.ForeColor = Theme.Muted;
        _clientIdInfo.Text = "Проверка Application ID…";
        try
        {
            using var response = await Http.Client.GetAsync(string.Format(AppInfo.DiscordAppInfoUrl, id));
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                _discordAppName = name;
                _checkedClientId = id;
                _clientIdInfo.ForeColor = Theme.Green;
                _clientIdInfo.Text = $"✔ Используется {source}, приложение «{name}». В Discord будет написано «Играет в {name}».";
            }
            else if ((int)response.StatusCode is 404 or 400)
            {
                _discordAppName = null;
                _clientIdInfo.ForeColor = Theme.Red;
                _clientIdInfo.Text = "✖ Приложение с таким Application ID не найдено. Проверьте ID в Discord Developer Portal.";
            }
            else
            {
                _clientIdInfo.ForeColor = Theme.Yellow;
                _clientIdInfo.Text = $"Не удалось проверить ID (HTTP {(int)response.StatusCode}). Используйте «Тест RPC».";
            }
        }
        catch (Exception ex)
        {
            _clientIdInfo.ForeColor = Theme.Yellow;
            _clientIdInfo.Text = "Не удалось проверить ID (нет доступа к discord.com): " + ex.Message;
        }
    }

    // ───────────────────────────── Страница «Интеграция» ─────────────────────────────

    Control BuildIntegrationPage()
    {
        var page = PageLayout(1);

        var dota = new Card("Папка Dota 2", "Определяется автоматически. Укажите её вручную, только если программа не нашла игру.");
        _dotaPathLabel = dota.Add(Ui.Value("…"));
        var detect = Dev(new FlatButton("Найти автоматически"));
        detect.Click += (_, _) => { ApplySetting(s => s.DotaPath = null); RefreshIntegrationNow(); };
        var browse = new FlatButton("Выбрать папку…");
        browse.Click += (_, _) => BrowseDotaFolder();
        var open = Dev(new FlatButton("Открыть папку"));
        open.Click += (_, _) =>
        {
            if (_gsiStatus?.DotaPath is { } p) Ui.OpenFolder(p);
        };
        dota.Add(Ui.ButtonRow(detect, browse, open));

        var gsi = Dev(new Card("Game State Integration",
            "Официальный механизм Valve: Dota 2 сама отправляет состояние матча программе на 127.0.0.1. " +
            "Нужен файл конфигурации в папке игры (устанавливается кнопкой ниже)."));
        _gsiStateLabel = gsi.Add(Ui.Value("…"));
        _gsiPathLabel = gsi.Add(Ui.Muted(""));
        var portRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, BackColor = Theme.Card, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        portRow.Controls.Add(new Label { Text = "Порт:", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 8, 8, 0) });
        _port = new NumericUpDown
        {
            Minimum = 1024,
            Maximum = 65535,
            Width = 100,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Font(10.5f),
            Margin = new Padding(0, 4, 10, 0),
        };
        var applyPort = new FlatButton("Применить порт");
        applyPort.Click += (_, _) => ApplyPort();
        portRow.Controls.Add(_port);
        portRow.Controls.Add(applyPort);
        gsi.Add(portRow);
        var install = new FlatButton("Установить / обновить", ButtonKind.Primary);
        install.Click += (_, _) =>
        {
            var (ok, text) = SetupRunner.InstallGsi(_app.Settings, interactive: true);
            MessageBox.Show(this, text + (ok ? "\n\nЕсли Dota 2 запущена, перезапустите игру, чтобы она прочитала конфигурацию." : ""),
                AppInfo.ProductName, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            RefreshIntegrationNow();
        };
        var remove = new FlatButton("Удалить конфигурацию", ButtonKind.Danger);
        remove.Click += (_, _) =>
        {
            try
            {
                var removed = GsiConfig.Remove(_app.Settings.DotaPath);
                MessageBox.Show(this, removed ? "Конфигурация GSI удалена." : "Конфигурация не найдена.", AppInfo.ProductName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось удалить: " + ex.Message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshIntegrationNow();
        };
        gsi.Add(Ui.ButtonRow(install, remove));

        var launch = new Card("Параметр запуска Steam",
            $"Без параметра {AppInfo.LaunchOption} Dota 2 не передаёт данные о матче. " +
            "Вручную: Steam → Библиотека → правый клик по Dota 2 → Свойства → Общие → Параметры запуска.");
        _launchLabel = launch.Add(Ui.Value("…"));
        _launchButton = new FlatButton("Добавить автоматически", ButtonKind.Primary);
        _launchButton.Click += async (_, _) =>
        {
            _launchButton.Enabled = false;
            try
            {
                var (ok, text) = await Task.Run(() => SetupRunner.EnsureLaunchOption(interactive: true));
                MessageBox.Show(this, text, AppInfo.ProductName, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally
            {
                _launchButton.Enabled = true;
                RefreshIntegrationNow();
            }
        };
        var copy = Dev(new FlatButton("Скопировать параметр"));
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(AppInfo.LaunchOption);
                MessageBox.Show(this, $"«{AppInfo.LaunchOption}» скопирован в буфер обмена.", AppInfo.ProductName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, AppInfo.ProductName);
            }
        };
        launch.Add(Ui.ButtonRow(_launchButton, copy));

        var diag = new Card("Если статус не появляется",
            "«Повторная настройка» заново подключит программу к Dota 2 (конфигурация игры и параметр запуска). После неё перезапустите Dota 2.");
        _diagLabel = Dev(diag.Add(Ui.Muted("")));
        var logs = Dev(new FlatButton("Открыть журнал"));
        logs.Click += (_, _) => Ui.OpenFolder(AppPaths.LogDir);
        var repair = new FlatButton("Повторная настройка");
        repair.Click += async (_, _) => await RepairAsync();
        diag.Add(Ui.ButtonRow(repair, logs));
        _tRecord = new ToggleSwitch();
        Dev(diag.Add(Ui.ToggleRow("Записывать образцы пакетов GSI",
            "По одному JSON на каждый этап/карту в папку gsi-samples (только на этом компьютере). Нужно для отладки новых режимов игры.", _tRecord)));
        _tRecord.CheckedChanged += (_, _) => ApplySetting(s => s.RecordGsiSamples = _tRecord.Checked);
        var samples = Dev(new FlatButton("Открыть образцы GSI"));
        samples.Click += (_, _) => Ui.OpenFolder(GsiRecorder.Folder);
        diag.Add(Ui.ButtonRow(samples));

        page.Controls.Add(Column(dota, gsi, launch, diag), 0, 0);
        return page;
    }

    void BrowseDotaFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите папку «dota 2 beta» (обычно ...\\steamapps\\common\\dota 2 beta)",
            UseDescriptionForTitle = true,
            InitialDirectory = _gsiStatus?.DotaPath ?? "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Допускаем выбор вложенной папки (game, game\dota, game\bin\win64).
        string? dir = dialog.SelectedPath;
        for (var i = 0; i < 4 && dir is not null && !SteamLocator.IsDotaDir(dir); i++) dir = Path.GetDirectoryName(dir);
        if (dir is null || !SteamLocator.IsDotaDir(dir))
        {
            MessageBox.Show(this, "В выбранной папке нет Dota 2 (ожидается подпапка game\\dota).", AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ApplySetting(s => s.DotaPath = dir);
        RefreshIntegrationNow();
    }

    void ApplyPort()
    {
        var port = (int)_port.Value;
        var wasInstalled = _gsiStatus?.State is GsiConfigState.Installed or GsiConfigState.Outdated;
        ApplySetting(s => s.GsiPort = port);
        if (wasInstalled)
        {
            var (ok, text) = SetupRunner.InstallGsi(_app.Settings, interactive: true);
            MessageBox.Show(this, (ok ? "Порт изменён, конфигурация GSI обновлена.\nПерезапустите Dota 2, если она запущена." : text),
                AppInfo.ProductName, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        RefreshIntegrationNow();
    }

    async Task RepairAsync()
    {
        var gsi = SetupRunner.InstallGsi(_app.Settings, interactive: true);
        var launch = await Task.Run(() => SetupRunner.EnsureLaunchOption(interactive: true));
        try { Autostart.Apply(_app.Settings.StartWithWindows, _app.Settings.StartWithDota); } catch (Exception ex) { Log.Error("Автозапуск", ex); }
        MessageBox.Show(this,
            (gsi.Ok ? "✔  " : "✖  ") + gsi.Text + "\n" + (launch.Ok ? "✔  " : "✖  ") + launch.Text,
            "Повторная настройка", MessageBoxButtons.OK, gsi.Ok && launch.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        RefreshIntegrationNow();
    }

    void RefreshIntegrationNow()
    {
        _lastIntegrationCheck = DateTime.MinValue;
        RefreshStatus();
    }

    // ───────────────────────────── Обновление ─────────────────────────────

    void StartIntegrationCheck()
    {
        if (_integrationCheckRunning || DateTime.UtcNow - _lastIntegrationCheck < TimeSpan.FromSeconds(5)) return;
        _integrationCheckRunning = true;
        _lastIntegrationCheck = DateTime.UtcNow;
        var settings = _app.Settings;
        Task.Run(() =>
        {
            GsiConfigStatus? gsi = null;
            List<LaunchOptionsInfo>? launch = null;
            AutostartStatus? autostart = null;
            try { gsi = GsiConfig.Check(settings.DotaPath, settings.GsiPort, settings.GsiToken); } catch (Exception ex) { Log.Error("Проверка GSI", ex); }
            try { launch = LaunchOptions.ReadAll(); } catch (Exception ex) { Log.Error("Проверка параметров запуска", ex); }
            try { autostart = Autostart.Query(); } catch (Exception ex) { Log.Error("Проверка автозапуска", ex); }
            return (gsi, launch, autostart);
        }).ContinueWith(t =>
        {
            _integrationCheckRunning = false;
            if (t.IsCompletedSuccessfully)
            {
                (_gsiStatus, _launchInfos, _autostart) = t.Result;
                RefreshStatus();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void RefreshUpdateUi()
    {
        var update = _app.AvailableUpdate;
        var skipped = update is not null && _app.Settings.SkippedUpdateVersion == update.Version.ToString();
        var show = update is not null && !skipped;
        if (_updateBanner.Visible != show) _updateBanner.Visible = show;
        if (show && !_updating)
            _updateBannerText.Text = $"Версия {update!.Version} — у вас {AppInfo.Version}. «Обновить сейчас» скачает установщик с GitHub, проверит его и обновит программу; настройки сохранятся.";
        if (_updating) return;
        var checkedAt = _app.LastUpdateCheckUtc is { } t ? $" Последняя проверка: {t.ToLocalTime():dd.MM HH:mm}." : "";
        _updateStatus.Text = update is not null
            ? $"Установлена версия {AppInfo.Version}. Доступна {update.Version}{(skipped ? " (пропущена)" : "")}.{checkedAt}"
            : $"Установлена версия {AppInfo.Version}{(_app.LastUpdateCheckUtc is null ? "." : " — это последняя версия.")}{checkedAt}";
    }

    async Task CheckUpdatesNowAsync(FlatButton button)
    {
        button.Enabled = false;
        _updateStatus.Text = "Проверка обновлений…";
        try
        {
            var update = await _app.CheckForUpdatesAsync(manual: true);
            if (update is not null && _app.Settings.SkippedUpdateVersion == update.Version.ToString())
                ApplySetting(s => s.SkippedUpdateVersion = null); // ручная проверка снова показывает пропущенную версию
            RefreshUpdateUi();
            if (update is not null) SelectPage(0);
        }
        catch (Exception ex)
        {
            _updateStatus.Text = "Не удалось проверить обновления: " + ex.Message;
        }
        finally
        {
            button.Enabled = true;
        }
    }

    async Task InstallUpdateAsync()
    {
        if (_app.AvailableUpdate is not { } update || _updating) return;
        _updating = true;
        _updateNow.Enabled = false;
        try
        {
            var progress = new Progress<int>(p => _updateBannerText.Text = $"Загрузка версии {update.Version}… {p}%");
            var setup = await UpdateChecker.DownloadAsync(update, progress);
            _updateBannerText.Text = "Установка… Программа перезапустится сама.";
            UpdateChecker.RunInstaller(setup); // установщик корректно закроет эту программу (--exit)
        }
        catch (Exception ex)
        {
            Log.Error("Обновление не удалось", ex);
            _updating = false;
            _updateNow.Enabled = true;
            _updateBannerText.Text = "Не удалось обновиться: " + ex.Message;
        }
    }

    void RefreshStatus()
    {
        RefreshUpdateUi();
        if (IsDisposed) return;
        StartIntegrationCheck();
        var st = _app.GetStatus();
        var settings = _app.Settings;

        // Discord
        var (dColor, dText) = st.Discord.State switch
        {
            DiscordState.Connected => (Theme.Green, st.Discord.UserName is { } u ? $"Подключено · {u}" : "Подключено"),
            DiscordState.Connecting => (Theme.Yellow, st.Discord.Message),
            DiscordState.Idle => (Theme.Gray, "Ожидание запуска Dota 2"),
            DiscordState.Unavailable => (Theme.Yellow, "Discord не запущен"),
            DiscordState.NotConfigured => (Theme.Red, "Не указан Application ID"),
            _ => (Theme.Red, "Ошибка"),
        };
        var dHint = st.Discord.State switch
        {
            DiscordState.Idle => "Подключение произойдёт автоматически при запуске игры",
            DiscordState.Unavailable => "Повторное подключение выполняется автоматически",
            DiscordState.NotConfigured => "Укажите его на вкладке «Настройки»",
            DiscordState.Error => st.Discord.Message,
            _ => null,
        };
        _stDiscord.Set(dColor, dText, dHint);
        _sidebarDot.Tag = dColor;
        _sidebarDot.Invalidate();
        _sidebarStatus.Text = "Discord: " + dText;

        // Dota
        _stDota.Set(st.DotaRunning ? Theme.Green : Theme.Gray,
            st.DotaRunning ? "Запущена" : "Не запущена",
            st.DotaStartedUtc is { } started ? $"С {started.ToLocalTime():HH:mm}" : "Программа ждёт запуска dota2.exe");

        // GSI
        if (!st.GsiListening)
            _stGsi.Set(Theme.Red, "Сервер не запущен", st.GsiError);
        else if (st.SnapshotFresh && st.LastGsiUtc is { } last)
            _stGsi.Set(Theme.Green, "Данные поступают", $"Последний пакет {Math.Max(0, (int)(DateTime.UtcNow - last).TotalSeconds)} с назад · порт {st.GsiPort}");
        else if (st.DotaRunning)
            _stGsi.Set(Theme.Yellow, "Нет данных от игры", $"Проверьте конфигурацию и параметр {AppInfo.LaunchOption}, затем перезапустите Dota 2");
        else
            _stGsi.Set(Theme.Gray, "Ожидание данных", $"Слушаю 127.0.0.1:{st.GsiPort}");

        // Конфигурация GSI
        if (_gsiStatus is { } g)
        {
            _stConfig.Set(g.State switch
            {
                GsiConfigState.Installed => Theme.Green,
                GsiConfigState.DotaNotFound => Theme.Red,
                _ => Theme.Yellow,
            }, g.Message, g.ConfigPath ?? "Укажите папку игры на вкладке «Интеграция»");
            _dotaPathLabel.Text = g.DotaPath ?? "Не найдена. Нажмите «Выбрать папку…» и укажите папку «dota 2 beta».";
            _dotaPathLabel.ForeColor = g.DotaPath is null ? Theme.Red : Theme.Text;
            _gsiStateLabel.Text = (g.State == GsiConfigState.Installed ? "✔ " : "✖ ") + g.Message;
            _gsiStateLabel.ForeColor = g.State == GsiConfigState.Installed ? Theme.Green : Theme.Yellow;
            _gsiPathLabel.Text = g.ConfigPath ?? "";
        }

        // Параметр запуска
        if (_launchInfos is { } infos)
        {
            if (infos.Count == 0)
            {
                _stLaunch.Set(Theme.Gray, "Профили Steam не найдены", "Добавьте параметр вручную в свойствах Dota 2");
                _launchLabel.Text = "Профили Steam не найдены.";
            }
            else
            {
                var main = infos[0];
                _stLaunch.Set(main.HasFlag ? Theme.Green : Theme.Yellow,
                    main.HasFlag ? "Задан" : "Не задан",
                    main.HasFlag ? $"Профиль {main.PersonaName ?? main.AccountId}" : "Добавьте его на вкладке «Интеграция»");
                _launchLabel.Text = settings.DeveloperMode
                    ? string.Join(Environment.NewLine, infos.Select(i =>
                        $"{(i.HasFlag ? "✔" : "✖")} {i.PersonaName ?? i.AccountId}{(i.IsMostRecent ? " (текущий)" : "")}: " +
                        (string.IsNullOrWhiteSpace(i.Options) ? "параметры запуска пустые" : i.Options)))
                    : main.HasFlag ? "✔ Параметр запуска задан" : "✖ Параметр запуска не задан — нажмите «Добавить автоматически»";
                _launchLabel.ForeColor = infos.All(i => i.HasFlag) ? Theme.Green : Theme.Text;
            }
        }

        // Автозапуск
        if (_autostart is { } a)
        {
            var expected = Autostart.ModeFor(settings.StartWithWindows, settings.StartWithDota);
            var text = expected switch
            {
                AutostartMode.Windows => "Вместе с Windows",
                AutostartMode.WithDota => "Вместе с Dota 2",
                _ => "Выключен",
            };
            if (a.DisabledInTaskManager && expected != AutostartMode.Off)
                _stAutostart.Set(Theme.Yellow, text, "Отключён в Диспетчере задач → Автозагрузка");
            else if (a.Registered != expected || !a.PathMatches)
                _stAutostart.Set(Theme.Yellow, text, "Запись в реестре не совпадает — переключите настройку ещё раз");
            else
                _stAutostart.Set(expected == AutostartMode.Off ? Theme.Gray : Theme.Green, text, null);
        }

        // Предпросмотр
        _preview.SetAppName(_discordAppName);
        var (emptyText, emptyHint) = !settings.ShowInMenu && st.DotaRunning
            ? ("Статус скрыт", "Вы в главном меню, а показ статуса в меню отключён в настройках.")
            : ("Статус не отображается", "Статус появится автоматически, когда вы запустите Dota 2.");
        _preview.SetActivity(st.Activity, emptyText, emptyHint);

        // Текущий матч
        var s = st.SnapshotFresh ? st.Snapshot : null;
        var inMatch = s is { InMatch: true, IsSpectating: false };
        SetMatch("Герой", inMatch && s!.HasHero ? _app.Heroes.DisplayName(s.HeroName!) : "—");
        SetMatch("Уровень", inMatch && s!.HeroLevel is > 0 ? s.HeroLevel.ToString()! : "—");
        SetMatch("K / D / A", inMatch && s!.Kills is { } k && s.Deaths is { } d && s.Assists is { } asst ? $"{k} / {d} / {asst}" : "—");
        SetMatch("Время матча", s is { InMatch: true } && s.ClockTime is int clock
            ? (clock < 0 ? "-" : "") + TimeSpan.FromSeconds(Math.Abs(clock)).ToString(clock >= 3600 ? @"h\:mm\:ss" : @"mm\:ss")
            : "—");
        SetMatch("Этап", s is null ? (st.DotaRunning ? "Нет данных GSI" : "Игра не запущена") : StageName(s));
        SetMatch("Счёт", s is { InMatch: true } && s.RadiantScore is { } r && s.DireScore is { } dd ? $"Свет {r} : {dd} Тьма" : "—");
        SetMatch("Последний матч", st.LastMatch is { } m
            ? $"{m.Hero ?? "?"} · {m.Kills}/{m.Deaths}/{m.Assists}{(m.Result is null ? "" : " · " + m.Result)}{(m.Minutes is null ? "" : $" · {m.Minutes} мин")} ({m.EndedLocal:HH:mm})"
            : "—");

        _diagLabel.Text =
            $"GSI: {(st.GsiListening ? $"http://127.0.0.1:{st.GsiPort}/" : "не запущен")} · пакетов получено: {st.GsiPackets}" +
            (st.LastGsiUtc is { } lg ? $" · последний: {lg.ToLocalTime():HH:mm:ss}" : "") +
            $"{Environment.NewLine}Журнал: {Log.FilePath}{Environment.NewLine}Настройки: {AppPaths.SettingsFile}";
    }

    void SetMatch(string key, string value)
    {
        if (_matchValues.TryGetValue(key, out var label) && label.Text != value) label.Text = value;
    }

    static string StageName(GsiSnapshot s)
    {
        if (s.IsSpectating) return "Наблюдение за матчем";
        if (s.IsHeroDemo) return "Проба героя";
        if (!s.InMatch) return "Главное меню";
        return s.ShortGameState switch
        {
            "GAME_IN_PROGRESS" => s.Paused ? "Матч идёт (пауза)" : "Матч идёт",
            "PRE_GAME" => "Подготовка к началу",
            "POST_GAME" => PresenceBuilder.MatchResult(s) is { } r ? $"Матч завершён — {r}" : "Матч завершён",
            "HERO_SELECTION" or "PLAYER_DRAFT" => "Выбор героя",
            "STRATEGY_TIME" => "Стратегия",
            "TEAM_SHOWCASE" => "Представление команд",
            "INIT" or "WAIT_FOR_PLAYERS_TO_LOAD" or "WAIT_FOR_MAP_TO_LOAD" => "Загрузка",
            "CUSTOM_GAME_SETUP" => "Настройка пользовательской игры",
            "DISCONNECT" => "Отключение",
            var other => other ?? "—",
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Закрытие окна сворачивает программу в трей; выход — через меню значка.
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnFormClosing(e);
    }

    public event Action? HiddenToTray;
}
