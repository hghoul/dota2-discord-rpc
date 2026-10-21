namespace Dota2RPC.Core;

/// <summary>Один экземпляр на пользователя + команды «показать окно» и «завершиться» для уже запущенного экземпляра.</summary>
internal sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\Dota2RPC.Instance";
    const string ShowEventName = @"Local\Dota2RPC.Show";
    const string ExitEventName = @"Local\Dota2RPC.Exit";

    readonly Mutex _mutex;
    readonly EventWaitHandle _show;
    readonly EventWaitHandle _exit;
    RegisteredWaitHandle? _showWait;
    RegisteredWaitHandle? _exitWait;
    bool _owned;

    SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _owned = true;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
    }

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero)) return new SingleInstance(mutex);
        }
        catch (AbandonedMutexException)
        {
            // Предыдущий экземпляр аварийно завершился — мьютекс теперь наш.
            return new SingleInstance(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public void Listen(Action onShow, Action onExit)
    {
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => onExit(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static bool IsRunning()
    {
        if (Mutex.TryOpenExisting(MutexName, out var m))
        {
            m.Dispose();
            return true;
        }
        return false;
    }

    public static void SignalShow() => Signal(ShowEventName);

    /// <summary>Просит запущенный экземпляр завершиться и ждёт освобождения мьютекса.</summary>
    public static bool SignalExitAndWait(TimeSpan timeout)
    {
        if (!IsRunning()) return true;
        Signal(ExitEventName);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning()) return true;
            Thread.Sleep(200);
        }
        return !IsRunning();
    }

    static void Signal(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var e))
            {
                using (e) e.Set();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось отправить сигнал {name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _exitWait?.Unregister(null);
        _show.Dispose();
        _exit.Dispose();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* не наш поток или уже освобождён */ }
            _owned = false;
        }
        _mutex.Dispose();
    }
}
