using System.Diagnostics;
using System.Text;
using Dota2RPC.Core;
using Microsoft.Win32;

namespace Dota2RPC.Steam;

/// <summary>Поиск Steam и Dota 2 по реестру, libraryfolders.vdf и запущенному процессу.</summary>
internal static class SteamLocator
{
    public static string? FindSteamPath()
    {
        foreach (var (hive, key, value) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                 })
        {
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(value) is string path && path.Length > 0)
                {
                    var full = Path.GetFullPath(path.Replace('/', '\\'));
                    if (Directory.Exists(full)) return full;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Реестр Steam ({key}): {ex.Message}");
            }
        }
        return null;
    }

    public static string? FindSteamExe(string? steamPath = null)
    {
        steamPath ??= FindSteamPath();
        if (steamPath is null) return null;
        var exe = Path.Combine(steamPath, "steam.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static bool IsSteamRunning() => ProcessExists("steam");

    public static bool ProcessExists(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public static bool IsDotaDir(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "game", "dota"));

    /// <summary>Возвращает корень установки Dota 2 (папка «dota 2 beta») или null.</summary>
    public static string? FindDotaPath(string? configured)
    {
        if (IsDotaDir(configured)) return Path.GetFullPath(configured!);

        var steam = FindSteamPath();
        if (steam is not null)
        {
            foreach (var library in LibraryFolders(steam))
            {
                var installDir = ReadInstallDir(library) ?? "dota 2 beta";
                var candidate = Path.Combine(library, "steamapps", "common", installDir);
                if (IsDotaDir(candidate)) return candidate;
            }
        }

        return FromRunningProcess();
    }

    /// <summary>Библиотеки Steam; библиотека, где числится приложение 570, — первой.</summary>
    public static List<string> LibraryFolders(string steamPath)
    {
        var withDota = new List<string>();
        var others = new List<string> { steamPath };
        var file = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(file))
            {
                var root = Vdf.Parse(File.ReadAllText(file, Encoding.UTF8));
                var folders = root["libraryfolders"] ?? root["LibraryFolders"];
                foreach (var entry in folders?.Children ?? [])
                {
                    var path = entry.IsBlock ? entry["path"]?.Value : entry.Value; // старый формат: "1" "D:\\Lib"
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                    var hasDota = entry["apps"]?[AppInfo.DotaAppId] is not null;
                    (hasDota ? withDota : others).Add(Path.GetFullPath(path));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать libraryfolders.vdf: {ex.Message}");
        }
        return withDota.Concat(others).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string? ReadInstallDir(string library)
    {
        var manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppInfo.DotaAppId}.acf");
        try
        {
            if (!File.Exists(manifest)) return null;
            return Vdf.Parse(File.ReadAllText(manifest, Encoding.UTF8)).Get("AppState", "installdir")?.Value;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать {manifest}: {ex.Message}");
            return null;
        }
    }

    static string? FromRunningProcess()
    {
        foreach (var p in Process.GetProcessesByName(AppInfo.DotaProcessName))
        {
            using (p)
            {
                try
                {
                    // ...\dota 2 beta\game\bin\win64\dota2.exe
                    var exe = p.MainModule?.FileName;
                    var root = exe is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "..", "..", ".."));
                    if (IsDotaDir(root)) return root;
                }
                catch
                {
                    // нет доступа к процессу — пропускаем
                }
            }
        }
        return null;
    }
}
