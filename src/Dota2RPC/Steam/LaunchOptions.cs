using System.Diagnostics;
using System.Text;
using Dota2RPC.Core;

namespace Dota2RPC.Steam;

internal sealed record LaunchOptionsInfo(string AccountId, string ConfigPath, string? PersonaName, string? Options, bool HasFlag, bool IsMostRecent);

/// <summary>
/// Параметры запуска Dota 2 хранятся Steam в userdata\&lt;id&gt;\config\localconfig.vdf
/// (UserLocalConfigStore → Software → Valve → Steam → apps → 570 → LaunchOptions).
/// Steam перезаписывает файл при выходе, поэтому изменять его можно только при закрытом Steam.
/// </summary>
internal static class LaunchOptions
{
    static readonly string[] AppsPath = ["UserLocalConfigStore", "Software", "Valve", "Steam"];

    public static bool HasFlag(string? options) =>
        !string.IsNullOrWhiteSpace(options) &&
        options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.Equals(AppInfo.LaunchOption, StringComparison.OrdinalIgnoreCase));

    public static List<LaunchOptionsInfo> ReadAll(string? steamPath = null)
    {
        var result = new List<LaunchOptionsInfo>();
        steamPath ??= SteamLocator.FindSteamPath();
        if (steamPath is null) return result;

        var mostRecent = MostRecentAccountId(steamPath);
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return result;

        foreach (var dir in Directory.EnumerateDirectories(userdata))
        {
            var id = Path.GetFileName(dir);
            if (id == "0" || !id.All(char.IsAsciiDigit)) continue;
            var file = Path.Combine(dir, "config", "localconfig.vdf");
            if (!File.Exists(file)) continue;
            try
            {
                var root = Vdf.Parse(File.ReadAllText(file, Encoding.UTF8));
                var options = root.Get(AppsPath)?["apps"]?[AppInfo.DotaAppId]?["LaunchOptions"]?.Value;
                var persona = root.Get("UserLocalConfigStore", "friends", "PersonaName")?.Value;
                result.Add(new LaunchOptionsInfo(id, file, persona, options, HasFlag(options), id == mostRecent));
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось прочитать {file}: {ex.Message}");
            }
        }
        return result.OrderByDescending(r => r.IsMostRecent).ToList();
    }

    /// <summary>
    /// Аккаунт, вошедший последним, в виде 32-битного ID: по флагу MostRecent = 1 (старые версии Steam)
    /// или по наибольшему timestamp (новые версии loginusers.vdf).
    /// </summary>
    static string? MostRecentAccountId(string steamPath)
    {
        try
        {
            var file = Path.Combine(steamPath, "config", "loginusers.vdf");
            if (!File.Exists(file)) return null;
            var users = Vdf.Parse(File.ReadAllText(file, Encoding.UTF8))["users"];
            var best = (Id: (string?)null, MostRecent: false, Time: -1L);
            foreach (var u in users?.Children ?? [])
            {
                if (!ulong.TryParse(u.Key, out var id64) || id64 <= 76561197960265728UL) continue;
                var mostRecent = u["MostRecent"]?.Value == "1";
                var time = long.TryParse(u["Timestamp"]?.Value, out var t) ? t : 0;
                if ((mostRecent && !best.MostRecent) || (mostRecent == best.MostRecent && time > best.Time))
                    best = ((id64 - 76561197960265728UL).ToString(), mostRecent, time);
            }
            return best.Id;
        }
        catch (Exception ex)
        {
            Log.Warn($"loginusers.vdf: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Возвращает текст localconfig.vdf с добавленным -gamestateintegration.
    /// Остальной файл сохраняется байт-в-байт: правка вносится только в нужное место.
    /// </summary>
    public static string AddFlag(string text)
    {
        var root = Vdf.Parse(text);
        var steam = root.Get(AppsPath) ?? throw new FormatException("В localconfig.vdf нет раздела Software/Valve/Steam");
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var apps = steam["apps"];
        if (apps is null)
        {
            var d = Depth(text, steam.CloseBrace) + 1;
            var block = Block(d, "apps", Block(d + 1, AppInfo.DotaAppId, KeyValue(d + 2, "LaunchOptions", AppInfo.LaunchOption, nl), nl), nl);
            return InsertBeforeLine(text, steam.CloseBrace, block);
        }

        var app = apps[AppInfo.DotaAppId];
        if (app is null)
        {
            var d = Depth(text, apps.CloseBrace) + 1;
            var block = Block(d, AppInfo.DotaAppId, KeyValue(d + 1, "LaunchOptions", AppInfo.LaunchOption, nl), nl);
            return InsertBeforeLine(text, apps.CloseBrace, block);
        }

        var options = app["LaunchOptions"];
        if (options is null || options.IsBlock)
        {
            if (options is { IsBlock: true }) throw new FormatException("LaunchOptions имеет неожиданный формат");
            var d = Depth(text, app.CloseBrace) + 1;
            return InsertBeforeLine(text, app.CloseBrace, KeyValue(d, "LaunchOptions", AppInfo.LaunchOption, nl));
        }

        if (HasFlag(options.Value)) return text;
        var updated = $"{options.Value} {AppInfo.LaunchOption}".Trim();
        return text[..options.ValueStart] + Vdf.Quote(updated) + text[options.ValueEnd..];
    }

    static int Depth(string text, int closeBrace)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, closeBrace - 1)) + 1;
        var tabs = 0;
        for (var i = lineStart; i < closeBrace && text[i] == '\t'; i++) tabs++;
        return tabs;
    }

    static string InsertBeforeLine(string text, int closeBrace, string insertion)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, closeBrace - 1)) + 1;
        // Если перед '}' на той же строке есть не только отступ, вставляем прямо перед скобкой.
        var prefix = text[lineStart..closeBrace];
        if (prefix.Trim().Length > 0) lineStart = closeBrace;
        return text[..lineStart] + insertion + text[lineStart..];
    }

    static string KeyValue(int depth, string key, string value, string nl) =>
        new string('\t', depth) + Vdf.Quote(key) + "\t\t" + Vdf.Quote(value) + nl;

    static string Block(int depth, string key, string inner, string nl)
    {
        var indent = new string('\t', depth);
        return indent + Vdf.Quote(key) + nl + indent + "{" + nl + inner + indent + "}" + nl;
    }

    /// <summary>Добавляет параметр во все профили Steam на компьютере. Steam должен быть закрыт.</summary>
    public static (int Changed, int Total, List<string> Errors) ApplyToAll(string? steamPath = null)
    {
        if (SteamLocator.IsSteamRunning())
            throw new InvalidOperationException("Steam запущен. Закройте Steam, иначе он перезапишет параметры запуска при выходе.");

        var errors = new List<string>();
        var changed = 0;
        var all = ReadAll(steamPath);
        foreach (var info in all)
        {
            if (info.HasFlag) continue;
            try
            {
                var original = File.ReadAllText(info.ConfigPath, Encoding.UTF8);
                var updated = AddFlag(original);
                if (updated == original) continue;

                // Проверяем, что результат по-прежнему корректный VDF и параметр действительно на месте.
                var check = Vdf.Parse(updated).Get(AppsPath)?["apps"]?[AppInfo.DotaAppId]?["LaunchOptions"]?.Value;
                if (!HasFlag(check)) throw new InvalidDataException("проверка результата не прошла");

                File.Copy(info.ConfigPath, info.ConfigPath + ".dota2rpc.bak", overwrite: true);
                var tmp = info.ConfigPath + ".dota2rpc.tmp";
                File.WriteAllText(tmp, updated, new UTF8Encoding(false));
                File.Move(tmp, info.ConfigPath, overwrite: true);
                changed++;
                Log.Info($"Параметр запуска добавлен для профиля {info.AccountId} ({info.PersonaName})");
            }
            catch (Exception ex)
            {
                errors.Add($"{info.PersonaName ?? info.AccountId}: {ex.Message}");
                Log.Error($"Не удалось изменить {info.ConfigPath}", ex);
            }
        }
        return (changed, all.Count, errors);
    }

    /// <summary>Корректно закрывает Steam (steam.exe -shutdown) и ждёт завершения процесса.</summary>
    public static bool ShutdownSteam(TimeSpan timeout)
    {
        var exe = SteamLocator.FindSteamExe();
        if (exe is null) return false;
        using (Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })) { }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (!SteamLocator.IsSteamRunning()) return true;
            Thread.Sleep(500);
        }
        return !SteamLocator.IsSteamRunning();
    }

    public static void StartSteam()
    {
        var exe = SteamLocator.FindSteamExe();
        if (exe is not null) using (Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })) { }
    }
}
