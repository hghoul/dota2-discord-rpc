using Microsoft.Win32;

namespace Dota2RPC.Core;

internal enum AutostartMode { Off, Windows, WithDota }

internal sealed record AutostartStatus(AutostartMode Registered, bool PathMatches, bool DisabledInTaskManager);

/// <summary>
/// Автозапуск через HKCU\...\Run (права администратора не нужны).
/// «Вместе с Windows» — программа стартует и сразу показывает значок в трее.
/// «Вместе с Dota 2» — программа незаметно ждёт запуска dota2.exe и только тогда включается.
/// </summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = AppInfo.ShortName;

    public const string BackgroundArg = "--background";
    public const string WatchArg = "--watch";

    public static AutostartMode ModeFor(bool withWindows, bool withDota) =>
        withWindows ? AutostartMode.Windows : withDota ? AutostartMode.WithDota : AutostartMode.Off;

    public static void Apply(bool withWindows, bool withDota)
    {
        var mode = ModeFor(withWindows, withDota);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (mode == AutostartMode.Off)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("Автозапуск отключён");
            return;
        }

        var arg = mode == AutostartMode.Windows ? BackgroundArg : WatchArg;
        key.SetValue(ValueName, $"\"{AppInfo.ExecutablePath}\" {arg}", RegistryValueKind.String);
        Log.Info($"Автозапуск: {mode}");
    }

    public static void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static AutostartStatus Query()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string command)
            return new AutostartStatus(AutostartMode.Off, true, false);

        var mode = command.Contains(WatchArg, StringComparison.OrdinalIgnoreCase) ? AutostartMode.WithDota : AutostartMode.Windows;
        var matches = command.Contains(AppInfo.ExecutablePath, StringComparison.OrdinalIgnoreCase);

        // Пользователь может отключить элемент в «Диспетчер задач → Автозагрузка»: первый байт 0x03 — отключено.
        var disabled = false;
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey))
        {
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } data) disabled = (data[0] & 1) == 1;
        }
        return new AutostartStatus(mode, matches, disabled);
    }
}
