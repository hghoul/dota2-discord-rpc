using Dota2RPC.Discord;
using Dota2RPC.Gsi;

namespace Dota2RPC.Core;

internal sealed record MatchSummary(string? Hero, string? HeroInternal, int? Kills, int? Deaths, int? Assists, string? Result, int? Minutes, DateTime EndedLocal);

internal sealed record AppStatus(
    DiscordStatus Discord,
    bool DotaRunning,
    DateTime? DotaStartedUtc,
    bool GsiListening,
    string? GsiError,
    int GsiPort,
    DateTime? LastGsiUtc,
    long GsiPackets,
    GsiSnapshot? Snapshot,
    bool SnapshotFresh,
    PresenceActivity? Activity,
    MatchSummary? LastMatch);

/// <summary>Связывает GSI, наблюдение за процессом, построитель статуса и Discord.</summary>
internal sealed class AppController : IDisposable
{
    // Для индикатора «данные поступают» в окне: пакет моложе этого считается свежим.
    static readonly TimeSpan GsiStaleAfter = TimeSpan.FromSeconds(75);

    readonly object _gate = new();
    readonly object _buildGate = new();
    readonly HeroCatalog _heroes;
    readonly PresenceBuilder _builder;
    readonly GsiServer _gsi;
    readonly ProcessWatcher _dota = new(AppInfo.DotaProcessName);
    readonly DiscordPresenceManager _discord = new();
    System.Threading.Timer? _tick;

    AppSettings _settings;
    GsiSnapshot? _snapshot;
    long _packets;
    PresenceActivity? _activity;
    MatchSummary? _lastMatch;
    string? _recordedMatchId;
    bool _disposed;

    public AppController(AppSettings settings, HeroCatalog? heroes = null)
    {
        _settings = settings;
        _heroes = heroes ?? new HeroCatalog();
        _builder = new PresenceBuilder(_heroes);
        _gsi = new GsiServer(() => Settings.GsiToken);
        _gsi.SnapshotReceived += OnSnapshot;
        _dota.Changed += OnDotaChanged;
        _discord.StatusChanged += () => StatusChanged?.Invoke();
    }

    public event Action? StatusChanged;
    public event Action<bool>? DotaRunningChanged;

    public HeroCatalog Heroes => _heroes;

    public AppSettings Settings
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    public void Start()
    {
        var s = Settings;
        SyncAutostart(s);
        _discord.Configure(s.EffectiveClientId);
        _discord.Start();
        _ = HeroAnimations.LoadAsync().ContinueWith(_ => Recompute(), TaskContinuationOptions.OnlyOnRanToCompletion);
        _ = DotaLogo.RefreshAsync().ContinueWith(t =>
        {
            if (t.Result) Recompute();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
        _gsi.Start(s.GsiPort);
        _dota.Start();
        _discord.SetActive(_dota.IsRunning);
        _tick = new System.Threading.Timer(_ => Recompute(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        // Первая проверка обновлений — через 20 с после запуска, дальше раз в 12 часов.
        _updateTimer = new System.Threading.Timer(_ => _ = CheckForUpdatesAsync(manual: false), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromHours(12));
        Recompute();
    }

    /// <summary>
    /// Приводит запись автозапуска в реестре к настройкам (например, если программу переустановили в другую папку).
    /// В тестовом режиме (DOTA2RPC_HOME) реестр пользователя не трогаем.
    /// </summary>
    static void SyncAutostart(AppSettings s)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTA2RPC_HOME"))) return;
        try
        {
            var expected = Autostart.ModeFor(s.StartWithWindows, s.StartWithDota);
            var actual = Autostart.Query();
            if (actual.Registered != expected || (expected != AutostartMode.Off && !actual.PathMatches))
                Autostart.Apply(s.StartWithWindows, s.StartWithDota);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось синхронизировать автозапуск", ex);
        }
    }

    /// <summary>Изменяет настройки, сохраняет их и применяет к работающим компонентам.</summary>
    public AppSettings UpdateSettings(Action<AppSettings> mutate)
    {
        AppSettings before, after;
        lock (_gate)
        {
            before = _settings;
            after = _settings.Clone();
            mutate(after);
            after.Normalize();
            after.Save();
            _settings = after;
        }

        if (before.GsiPort != after.GsiPort) _gsi.Start(after.GsiPort);
        if (before.EffectiveClientId != after.EffectiveClientId) _discord.Configure(after.EffectiveClientId);
        if (before.StartWithWindows != after.StartWithWindows || before.StartWithDota != after.StartWithDota)
            Autostart.Apply(after.StartWithWindows, after.StartWithDota);

        Recompute();
        StatusChanged?.Invoke();
        return after.Clone();
    }

    void OnSnapshot(GsiSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            _packets++;
            TrackMatch(snapshot);
        }
        var settings = Settings;
        if (settings.RecordGsiSamples) GsiRecorder.Save(snapshot);
        if (!settings.GsiConfirmed)
        {
            // Первый пакет от игры: дальше тишина при запущенной Dota трактуется как главное меню.
            try { UpdateSettings(s => s.GsiConfirmed = true); } catch (Exception ex) { Log.Error("Не удалось сохранить признак GSI", ex); }
            Log.Info("GSI: получены первые данные от игры — интеграция подтверждена");
        }
        Recompute();
    }

    void TrackMatch(GsiSnapshot s)
    {
        if (s.ShortGameState != "POST_GAME" || s.IsSpectating || string.IsNullOrEmpty(s.MatchId) || s.MatchId == _recordedMatchId)
            return;
        _recordedMatchId = s.MatchId;
        _lastMatch = new MatchSummary(
            s.HasHero ? _heroes.DisplayName(s.HeroName!) : null,
            s.HeroName,
            s.Kills, s.Deaths, s.Assists,
            PresenceBuilder.MatchResult(s),
            s.ClockTime is int c && c > 0 ? c / 60 : null,
            DateTime.Now);
        Log.Info($"Матч {s.MatchId} завершён: {_lastMatch.Hero} {s.Kills}/{s.Deaths}/{s.Assists} {_lastMatch.Result}");
    }

    void OnDotaChanged()
    {
        var running = _dota.IsRunning;
        if (!running)
        {
            lock (_gate) _snapshot = null; // данные прошлой сессии не должны «всплыть» при следующем запуске
        }
        _discord.SetActive(running);
        Recompute();
        DotaRunningChanged?.Invoke(running);
        StatusChanged?.Invoke();
    }

    void Recompute()
    {
        if (_disposed) return;
        try
        {
            GsiSnapshot? snapshot;
            AppSettings settings;
            lock (_gate)
            {
                snapshot = _snapshot;
                settings = _settings;
            }
            var now = DateTime.UtcNow;

            // Последний пакет остаётся актуальным, пока игра запущена: Dota молчит в меню и на паузе,
            // а данные прошлого запуска сбрасываются при закрытии игры (OnDotaChanged).
            PresenceActivity? activity;
            lock (_buildGate)
            {
                activity = _builder.Build(new PresenceInput(snapshot, _dota.IsRunning, _dota.StartedUtc, now, settings, settings.GsiConfirmed));
            }

            lock (_gate) _activity = activity;
            _discord.SetActivity(activity);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка построения статуса", ex);
        }
    }

    public AppStatus GetStatus()
    {
        lock (_gate)
        {
            var fresh = _snapshot is not null && DateTime.UtcNow - _snapshot.ReceivedUtc < GsiStaleAfter;
            return new AppStatus(
                _discord.Status,
                _dota.IsRunning,
                _dota.StartedUtc,
                _gsi.IsListening,
                _gsi.LastError,
                _gsi.Port,
                _snapshot?.ReceivedUtc,
                _packets,
                _snapshot,
                fresh,
                _activity,
                _lastMatch);
        }
    }

    System.Threading.Timer? _updateTimer;
    int _updateCheckRunning;

    /// <summary>Найденная новая версия (или null).</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }

    public DateTime? LastUpdateCheckUtc { get; private set; }

    public string? LastUpdateError { get; private set; }

    /// <summary>Новая версия найдена (и пользователь не просил её пропустить, если проверка автоматическая).</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <returns>Новая версия или null, если её нет.</returns>
    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool manual)
    {
        var settings = Settings;
        if (!manual && !settings.CheckUpdates) return null;
        if (Interlocked.Exchange(ref _updateCheckRunning, 1) == 1) return AvailableUpdate;
        try
        {
            var update = await UpdateChecker.CheckAsync().ConfigureAwait(false);
            LastUpdateCheckUtc = DateTime.UtcNow;
            LastUpdateError = null;
            AvailableUpdate = update;
            if (update is not null)
            {
                Log.Info($"Обновление: доступна версия {update.Version} (сейчас {UpdateChecker.CurrentVersion})");
                if (manual || settings.SkippedUpdateVersion != update.Version.ToString()) UpdateAvailable?.Invoke(update);
            }
            StatusChanged?.Invoke();
            return update;
        }
        catch (Exception ex)
        {
            LastUpdateError = ex.Message;
            Log.Warn($"Обновление: не удалось проверить: {ex.Message}");
            if (manual) throw;
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _updateCheckRunning, 0);
        }
    }

    public static PresenceActivity CreateTestActivity() => new()
    {
        Details = "🧪 Проверка Rich Presence",
        State = "✅ Соединение с Discord работает",
        // Если анимированные иконки подключены — показываем одну из них, чтобы их можно было проверить без матча.
        LargeImage = HeroAnimations.TryGetUrl("pudge") ?? AppInfo.DotaLogoUrl,
        LargeText = "Dota 2 Discord Rich Presence",
        SmallImage = HeroAnimations.TryGetUrl("pudge") is null ? null : AppInfo.DotaLogoUrl,
        SmallText = "Dota 2",
        StartUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    public Task<RpcResult> TestRpcAsync() => _discord.TestAsync(CreateTestActivity());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick?.Dispose();
        _updateTimer?.Dispose();
        _dota.Dispose();
        _gsi.Dispose();
        _discord.Dispose(); // очищает статус в Discord
        Log.Info("Контроллер остановлен");
    }
}
