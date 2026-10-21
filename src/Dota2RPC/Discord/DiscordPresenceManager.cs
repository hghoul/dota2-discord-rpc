using Dota2RPC.Core;

namespace Dota2RPC.Discord;

internal enum DiscordState
{
    NotConfigured,
    Idle,
    Connecting,
    Connected,
    Unavailable,
    Error,
}

internal sealed record DiscordStatus(DiscordState State, string Message, string? UserName = null);

/// <summary>
/// Управляет соединением с Discord в фоновом цикле: подключение только когда это нужно (запущена Dota 2
/// или идёт тест), переподключение с экспоненциальной задержкой, ограничение частоты SET_ACTIVITY,
/// очистка статуса при выходе. Все сетевые операции — с таймаутами, поэтому цикл не может зависнуть.
/// </summary>
internal sealed class DiscordPresenceManager : IDisposable
{
    // Discord ограничивает SET_ACTIVITY примерно 5 обновлениями за 20 секунд.
    static readonly TimeSpan MinSendInterval = TimeSpan.FromSeconds(4);
    static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(20);

    readonly object _gate = new();
    readonly SemaphoreSlim _signal = new(0, 1);
    readonly CancellationTokenSource _cts = new();
    Task? _loop;
    volatile bool _stopping;

    string _clientId = "";
    bool _active;
    PresenceActivity? _desired;
    PresenceActivity? _testActivity;
    DateTime _testUntilUtc;
    TaskCompletionSource<RpcResult>? _testTcs;
    DateTime _nextAttemptUtc = DateTime.MinValue;
    DiscordStatus _status = new(DiscordState.NotConfigured, "Application ID не указан");

    public event Action? StatusChanged;

    public DiscordStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public void Configure(string clientId)
    {
        lock (_gate)
        {
            if (_clientId == clientId) return;
            _clientId = clientId;
            _nextAttemptUtc = DateTime.MinValue;
        }
        Wake();
    }

    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_active == active) return;
            _active = active;
            if (active) _nextAttemptUtc = DateTime.MinValue;
        }
        Wake();
    }

    public void SetActivity(PresenceActivity? activity)
    {
        lock (_gate) _desired = activity;
        Wake();
    }

    /// <summary>Показывает тестовый статус на 20 секунд и возвращает ответ Discord.</summary>
    public async Task<RpcResult> TestAsync(PresenceActivity activity)
    {
        TaskCompletionSource<RpcResult> tcs;
        lock (_gate)
        {
            _testTcs?.TrySetResult(RpcResult.Failed("Тест прерван новым тестом", false));
            tcs = new TaskCompletionSource<RpcResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _testTcs = tcs;
            _testActivity = activity;
            // Окно на подключение: Discord проверяет Application ID по сети, это может занять десятки секунд.
            // После подтверждения тестовый статус показывается ещё TestDuration.
            _testUntilUtc = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            _nextAttemptUtc = DateTime.MinValue;
        }
        Wake();
        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return RpcResult.Failed("Discord не ответил за 25 секунд", false);
        }
    }

    void Wake()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    void SetStatus(DiscordState state, string message, string? user = null)
    {
        bool changed;
        lock (_gate)
        {
            var next = new DiscordStatus(state, message, user);
            changed = next != _status;
            _status = next;
        }
        if (changed)
        {
            Log.Info($"Discord: {state} — {message}");
            try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.Error("Discord: ошибка обработчика статуса", ex); }
        }
    }

    void CompleteTest(TaskCompletionSource<RpcResult>? tcs, RpcResult result)
    {
        if (tcs is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_testTcs, tcs))
            {
                _testTcs = null;
                if (result.Ok) _testUntilUtc = DateTime.UtcNow + TestDuration;
                else _testActivity = null; // тест не удался — не пытаемся бесконечно
            }
        }
        tcs.TrySetResult(result);
    }

    async Task RunAsync(CancellationToken ct)
    {
        DiscordIpcClient? client = null;
        var connectedClientId = "";
        var lastSent = (string?)null; // null — ещё ничего не отправлено в этом соединении
        var lastSendUtc = DateTime.MinValue;
        string? lastLoggedText = null;
        var failures = 0;

        while (!_stopping && !ct.IsCancellationRequested)
        {
            var wait = TimeSpan.FromSeconds(30);
            try
            {
                string clientId;
                bool active, testing;
                PresenceActivity? desired;
                TaskCompletionSource<RpcResult>? testTcs;
                DateTime nextAttempt, testUntil;
                var now = DateTime.UtcNow;
                lock (_gate)
                {
                    clientId = _clientId;
                    if (_testActivity is not null && now >= _testUntilUtc) _testActivity = null;
                    testing = _testActivity is not null;
                    testUntil = _testUntilUtc;
                    active = _active || testing;
                    desired = testing ? _testActivity : _desired;
                    testTcs = _testTcs;
                    nextAttempt = _nextAttemptUtc;
                }

                // Соединение разорвано или сменился Application ID — закрываем старое.
                if (client is not null && (!client.IsConnected || clientId != connectedClientId))
                {
                    var reason = client.IsConnected ? "смена Application ID" : client.CloseReason ?? "соединение потеряно";
                    Log.Info($"Discord: переподключение ({reason})");
                    await CloseAsync(client, clear: client.IsConnected).ConfigureAwait(false);
                    client = null;
                    lastSent = null;
                }

                if (!AppSettings.IsValidClientId(clientId))
                {
                    SetStatus(DiscordState.NotConfigured, "Укажите Application ID в настройках");
                    CompleteTest(testTcs, RpcResult.Failed("Не указан Application ID (Client ID) приложения Discord", false));
                    wait = Timeout.InfiniteTimeSpan;
                }
                else if (!active)
                {
                    if (client is not null)
                    {
                        await CloseAsync(client, clear: true).ConfigureAwait(false);
                        client = null;
                        lastSent = null;
                    }
                    SetStatus(DiscordState.Idle, "Ожидание запуска Dota 2");
                    wait = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    if (client is null)
                    {
                        if (now < nextAttempt)
                        {
                            wait = nextAttempt - now;
                        }
                        else
                        {
                            SetStatus(DiscordState.Connecting, "Подключение к Discord…");
                            try
                            {
                                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                connectTimeout.CancelAfter(TimeSpan.FromSeconds(50));
                                client = await DiscordIpcClient.ConnectAsync(clientId, connectTimeout.Token).ConfigureAwait(false);
                                client.Closed += Wake; // мгновенно реагируем на выход/перезапуск Discord
                                connectedClientId = clientId;
                                failures = 0;
                                lastSent = null;
                                SetStatus(DiscordState.Connected, "Подключено", client.UserName);
                            }
                            catch (DiscordRpcException ex)
                            {
                                failures++;
                                ScheduleRetry(ex.Code == 4000 ? TimeSpan.FromSeconds(60) : Backoff(failures));
                                SetStatus(DiscordState.Error, ex.FriendlyMessage);
                                CompleteTest(testTcs, RpcResult.Failed(ex.FriendlyMessage, false));
                            }
                            catch (DiscordUnavailableException ex)
                            {
                                failures++;
                                ScheduleRetry(Backoff(failures));
                                SetStatus(DiscordState.Unavailable, ex.Message);
                                CompleteTest(testTcs, RpcResult.Failed(ex.Message, false));
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                failures++;
                                ScheduleRetry(Backoff(failures));
                                SetStatus(DiscordState.Unavailable, "Discord не ответил на подключение (таймаут)");
                                CompleteTest(testTcs, RpcResult.Failed("Discord не ответил на подключение", false));
                            }
                            wait = TimeSpan.Zero;
                        }
                    }
                    else
                    {
                        var signature = desired?.Signature ?? "<clear>";
                        if (signature != lastSent)
                        {
                            var since = now - lastSendUtc;
                            if (since < MinSendInterval)
                            {
                                wait = MinSendInterval - since;
                            }
                            else
                            {
                                lastSendUtc = now;
                                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                sendTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                                var result = await client.SetActivityAsync(desired?.ToJson(), sendTimeout.Token).ConfigureAwait(false);
                                if (result.Ok)
                                {
                                    var text = desired is null ? "(очищен)" : $"{desired.Details} | {desired.State}";
                                    if (text != lastLoggedText)
                                    {
                                        // Пишем в журнал только смену текста, а не каждый сдвиг таймера.
                                        Log.Info($"Discord: статус принят: {text}");
                                        lastLoggedText = text;
                                    }
                                    lastSent = signature;
                                    if (testing) CompleteTest(testTcs, RpcResult.Success("Discord подтвердил тестовый статус"));
                                    SetStatus(DiscordState.Connected, "Подключено", client.UserName);
                                }
                                else if (result.ConnectionLost)
                                {
                                    Log.Warn("Discord: " + result.Message);
                                    await CloseAsync(client, clear: false).ConfigureAwait(false);
                                    client = null;
                                    lastSent = null;
                                    ScheduleRetry(TimeSpan.FromSeconds(2));
                                    if (testing) CompleteTest(testTcs, result);
                                }
                                else
                                {
                                    // Discord отклонил содержимое — не повторяем то же самое бесконечно.
                                    lastSent = signature;
                                    Log.Warn("Discord: " + result.Message);
                                    SetStatus(DiscordState.Error, result.Message, client.UserName);
                                    if (testing) CompleteTest(testTcs, result);
                                }
                                wait = TimeSpan.Zero;
                            }
                        }
                        else if (testing)
                        {
                            CompleteTest(testTcs, RpcResult.Success("Discord подтвердил тестовый статус"));
                        }

                        if (testing)
                        {
                            var left = testUntil - now;
                            if (left > TimeSpan.Zero && left < wait) wait = left;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Discord: непредвиденная ошибка цикла", ex);
                if (client is not null)
                {
                    await CloseAsync(client, clear: false).ConfigureAwait(false);
                    client = null;
                    lastSent = null;
                }
                ScheduleRetry(TimeSpan.FromSeconds(5));
                wait = TimeSpan.FromSeconds(5);
            }

            if (wait != TimeSpan.Zero && !_stopping)
            {
                try
                {
                    await _signal.WaitAsync(wait, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        if (client is not null) await CloseAsync(client, clear: true).ConfigureAwait(false);
    }

    void ScheduleRetry(TimeSpan delay)
    {
        lock (_gate) _nextAttemptUtc = DateTime.UtcNow + delay;
    }

    static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));

    static async Task CloseAsync(DiscordIpcClient client, bool clear)
    {
        if (clear && client.IsConnected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await client.SetActivityAsync(null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Discord: не удалось очистить статус: {ex.Message}");
            }
        }
        await client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Корректное завершение: очищает статус в Discord и закрывает канал (не дольше ~5 секунд).</summary>
    public void Dispose()
    {
        _stopping = true;
        Wake();
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* завершаемся в любом случае */ }
        _cts.Cancel();
        lock (_gate) _testTcs?.TrySetResult(RpcResult.Failed("Программа завершается", false));
    }
}
