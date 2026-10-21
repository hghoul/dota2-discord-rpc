using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Dota2RPC.Gsi;
using Dota2RPC.Steam;

namespace Dota2RPC.Core;

/// <summary>
/// Неинтерактивная настройка, вызываемая установщиком и пунктом «Повторная настройка»:
///   --setup [--client-id=ID] [--autostart=0|1] [--with-dota=0|1] [--gsi=0|1] [--launch-option=0|1] [--quiet]
///   --uninstall            — остановить программу, удалить конфиг GSI и автозапуск
///   --write-gsi=PATH --port=N --token=T — служебный режим с повышенными правами
/// </summary>
internal static class SetupRunner
{
    public const int ExitOk = 0;
    public const int ExitPartial = 1;
    public const int ExitError = 2;

    public static int RunSetup(CommandLine cli)
    {
        var report = new List<(bool Ok, string Text)>();
        var settings = AppSettings.Load();

        if (cli.Get("client-id") is { } id)
        {
            id = id.Trim();
            if (id.Length == 0) { }
            else if (AppSettings.IsValidClientId(id)) settings.DiscordClientId = id;
            else report.Add((false, $"Application ID «{id}» некорректен (нужно 17–20 цифр). Укажите его позже в настройках."));
        }
        if (cli.GetBool("autostart") is { } autostart) settings.StartWithWindows = autostart;
        if (cli.GetBool("with-dota") is { } withDota) settings.StartWithDota = withDota;
        settings.Normalize();

        if (settings.TrySave()) report.Add((true, "Настройки сохранены"));
        else report.Add((false, "Не удалось сохранить настройки (см. журнал)"));

        try
        {
            Autostart.Apply(settings.StartWithWindows, settings.StartWithDota);
            report.Add((true, Autostart.ModeFor(settings.StartWithWindows, settings.StartWithDota) switch
            {
                AutostartMode.Windows => "Автозапуск вместе с Windows включён",
                AutostartMode.WithDota => "Запуск вместе с Dota 2 включён",
                _ => "Автозапуск отключён",
            }));
        }
        catch (Exception ex)
        {
            report.Add((false, "Автозапуск: " + ex.Message));
        }

        if (cli.GetBool("gsi") ?? true)
            report.Add(InstallGsi(settings, interactive: !cli.Has("quiet")));

        if (cli.GetBool("launch-option") ?? false)
            report.Add(EnsureLaunchOption(interactive: !cli.Has("quiet")));

        if (!AppSettings.IsValidClientId(settings.EffectiveClientId))
            report.Add((false, "Application ID Discord не указан — укажите его в окне программы (вкладка «Настройки»)."));

        var ok = report.All(r => r.Ok);
        if (!cli.Has("quiet"))
        {
            var text = new StringBuilder();
            foreach (var (good, line) in report) text.AppendLine((good ? "✔  " : "✖  ") + line);
            MessageBox.Show(text.ToString(), ok ? "Настройка завершена" : "Настройка завершена с замечаниями",
                MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        foreach (var (good, line) in report) Log.Info($"Setup: {(good ? "OK" : "FAIL")} {line}");
        return ok ? ExitOk : ExitPartial;
    }

    public static (bool Ok, string Text) InstallGsi(AppSettings settings, bool interactive)
    {
        var dota = SteamLocator.FindDotaPath(settings.DotaPath);
        if (dota is null)
            return (false, "Dota 2 не найдена. Укажите папку игры вручную: окно программы → «Интеграция» → «Выбрать папку…».");

        try
        {
            var path = GsiConfig.Install(dota, settings.GsiPort, settings.GsiToken);
            var restart = SteamLocator.ProcessExists(AppInfo.DotaProcessName) ? $"{Environment.NewLine}Перезапустите Dota 2, чтобы игра прочитала её." : "";
            return (true, $"Конфигурация GSI установлена: {path}{restart}");
        }
        catch (UnauthorizedAccessException)
        {
            if (interactive && MessageBox.Show(
                    $"Нет прав на запись в папку Dota 2:\n{dota}\n\nПовторить с правами администратора?",
                    AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                var code = RunElevated($"--write-gsi=\"{dota}\" --port={settings.GsiPort} --token={settings.GsiToken}");
                if (code == ExitOk) return (true, "Конфигурация GSI установлена (с правами администратора)");
                return (false, code == -1 ? "Повышение прав отменено — конфигурация GSI не установлена" : "Не удалось записать конфигурацию GSI даже с правами администратора");
            }
            return (false, $"Нет прав на запись в {dota}. Запустите «Повторную настройку» от имени администратора.");
        }
        catch (Exception ex)
        {
            return (false, "Ошибка установки конфигурации GSI: " + ex.Message);
        }
    }

    public static (bool Ok, string Text) EnsureLaunchOption(bool interactive)
    {
        try
        {
            var infos = LaunchOptions.ReadAll();
            if (infos.Count == 0)
                return (false, $"Профили Steam не найдены. Добавьте параметр {AppInfo.LaunchOption} в свойства Dota 2 в Steam вручную.");
            if (infos.All(i => i.HasFlag))
                return (true, $"Параметр запуска {AppInfo.LaunchOption} уже задан");

            var restartSteam = false;
            if (SteamLocator.IsSteamRunning())
            {
                if (!interactive)
                    return (false, $"Steam запущен — параметр {AppInfo.LaunchOption} не добавлен. Добавьте его в программе (вкладка «Интеграция»).");
                var answer = MessageBox.Show(
                    $"Чтобы добавить параметр запуска {AppInfo.LaunchOption}, Steam нужно закрыть (иначе он перезапишет изменения).\n\n" +
                    "Закрыть Steam сейчас? Он будет запущен снова автоматически.\n\n«Нет» — добавить параметр вручную позже.",
                    AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                    return (false, $"Параметр {AppInfo.LaunchOption} не добавлен: Steam → Dota 2 → Свойства → Параметры запуска.");
                if (SteamLocator.ProcessExists(AppInfo.DotaProcessName))
                    return (false, "Сначала закройте Dota 2, затем повторите добавление параметра запуска.");
                if (!LaunchOptions.ShutdownSteam(TimeSpan.FromSeconds(45)))
                    return (false, "Steam не закрылся за 45 секунд. Закройте его вручную и повторите.");
                restartSteam = true;
            }

            var (changed, total, errors) = LaunchOptions.ApplyToAll();
            if (restartSteam) LaunchOptions.StartSteam();
            if (errors.Count > 0)
                return (false, "Параметр запуска добавлен не везде: " + string.Join("; ", errors));
            return (true, $"Параметр запуска {AppInfo.LaunchOption} добавлен (профилей изменено: {changed} из {total})");
        }
        catch (Exception ex)
        {
            Log.Error("Параметр запуска", ex);
            return (false, "Не удалось добавить параметр запуска: " + ex.Message);
        }
    }

    /// <summary>Служебный режим: запись конфига GSI в процессе с повышенными правами.</summary>
    public static int RunWriteGsi(CommandLine cli)
    {
        try
        {
            var path = cli.Get("write-gsi");
            if (path is null || !int.TryParse(cli.Get("port"), out var port) || cli.Get("token") is not { Length: >= 16 } token)
                return ExitError;
            GsiConfig.Install(path, port, token);
            return ExitOk;
        }
        catch (Exception ex)
        {
            Log.Error("Запись GSI с повышенными правами", ex);
            return ExitError;
        }
    }

    /// <returns>Код выхода процесса или -1, если пользователь отменил UAC.</returns>
    static int RunElevated(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(AppInfo.ExecutablePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (p is null) return ExitError;
            if (!p.WaitForExit(TimeSpan.FromSeconds(60))) return ExitError;
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return -1;
        }
    }

    public static int RunUninstall()
    {
        // 1. Останавливаем работающий экземпляр (он сам очистит статус в Discord).
        if (!SingleInstance.SignalExitAndWait(TimeSpan.FromSeconds(8)))
            Log.Warn("Uninstall: экземпляр не завершился сам, завершаем принудительно");

        // Остальные процессы программы (например, незакрытое окно настройки) иначе заблокируют удаление файлов.
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(AppInfo.ShortName))
        {
            using (p)
            {
                try
                {
                    if (p.Id == self) continue;
                    p.Kill();
                    p.WaitForExit(3000);
                    Log.Info($"Uninstall: завершён процесс {p.Id}");
                }
                catch { /* уже завершён или нет доступа */ }
            }
        }

        // 2. Удаляем автозапуск и конфигурацию GSI.
        try { Autostart.Remove(); } catch (Exception ex) { Log.Error("Uninstall: автозапуск", ex); }
        try
        {
            var settings = AppSettings.Load();
            GsiConfig.Remove(settings.DotaPath);
        }
        catch (Exception ex)
        {
            Log.Error("Uninstall: конфигурация GSI", ex);
        }
        Log.Info("Uninstall: очистка завершена");
        return ExitOk;
    }
}

/// <summary>Разбор аргументов вида --key=value и --flag.</summary>
internal sealed class CommandLine
{
    readonly Dictionary<string, string?> _args = new(StringComparer.OrdinalIgnoreCase);

    public CommandLine(IEnumerable<string> args)
    {
        foreach (var raw in args)
        {
            if (!raw.StartsWith("--", StringComparison.Ordinal)) continue;
            var body = raw[2..];
            var eq = body.IndexOf('=');
            if (eq < 0) _args[body] = null;
            else _args[body[..eq]] = body[(eq + 1)..].Trim('"');
        }
    }

    public bool Has(string key) => _args.ContainsKey(key);

    public string? Get(string key) => _args.TryGetValue(key, out var v) ? v : null;

    public bool? GetBool(string key) => Get(key)?.ToLowerInvariant() switch
    {
        "1" or "true" or "yes" => true,
        "0" or "false" or "no" => false,
        _ => Has(key) ? true : null,
    };
}
