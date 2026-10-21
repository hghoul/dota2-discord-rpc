using System.Diagnostics;

namespace Dota2RPC.Core;

/// <summary>
/// Следит за процессом dota2.exe. Пока игра не запущена — редкий опрос списка процессов (раз в 3 с).
/// Когда игра найдена, программа держит её дескриптор и узнаёт о выходе по событию Exited,
/// поэтому во время игры перебора всех процессов системы нет (контрольная проверка — раз в 30 с).
/// </summary>
internal sealed class ProcessWatcher(string processName) : IDisposable
{
    static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(3);
    static readonly TimeSpan RunningPoll = TimeSpan.FromSeconds(30);

    readonly object _gate = new();
    System.Threading.Timer? _timer;
    Process? _tracked;
    int _checking;
    bool _disposed;

    public bool IsRunning { get; private set; }
    public DateTime? StartedUtc { get; private set; }

    public event Action? Changed;

    public void Start()
    {
        _timer = new System.Threading.Timer(_ => Check(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Check();
    }

    void Check()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var processes = Process.GetProcessesByName(processName);
            Process? first = null;
            DateTime? started = null;
            foreach (var p in processes)
            {
                try
                {
                    var t = p.StartTime.ToUniversalTime();
                    if (started is null || t < started)
                    {
                        started = t;
                        first = p;
                    }
                }
                catch
                {
                    first ??= p; // время запуска недоступно — не критично
                }
            }
            foreach (var p in processes)
                if (!ReferenceEquals(p, first)) p.Dispose();

            var running = first is not null;
            if (running) Track(first!);
            else Untrack();

            if (running != IsRunning)
            {
                IsRunning = running;
                StartedUtc = running ? started ?? DateTime.UtcNow : null;
                Log.Info(running ? $"Dota 2 запущена (с {StartedUtc:HH:mm:ss} UTC)" : "Dota 2 закрыта");
                Changed?.Invoke();
            }

            lock (_gate)
            {
                if (!_disposed) _timer?.Change(running ? RunningPoll : IdlePoll, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка проверки процесса Dota 2", ex);
            lock (_gate)
            {
                if (!_disposed) _timer?.Change(IdlePoll, Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    void Track(Process process)
    {
        lock (_gate)
        {
            if (_tracked is not null && _tracked.Id == process.Id)
            {
                process.Dispose();
                return;
            }
            UntrackLocked();
            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += OnExited;
                _tracked = process;
            }
            catch
            {
                // нет прав на дескриптор — остаётся опрос
                process.Dispose();
            }
        }
    }

    void OnExited(object? sender, EventArgs e) => ThreadPool.QueueUserWorkItem(_ => Check());

    void Untrack()
    {
        lock (_gate) UntrackLocked();
    }

    void UntrackLocked()
    {
        if (_tracked is null) return;
        _tracked.Exited -= OnExited;
        _tracked.Dispose();
        _tracked = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            UntrackLocked();
        }
    }
}
