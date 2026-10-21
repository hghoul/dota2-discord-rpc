using System.Reflection;

namespace Dota2RPC.Core;

internal static class AppInfo
{
    public const string ProductName = "Dota 2 Discord Rich Presence";
    public const string ShortName = "Dota2RPC";

    public const string GsiConfigFileName = "gamestate_integration_dota2rpc.cfg";
    public const string GsiConfigBlockName = "Dota2RPC Discord Rich Presence";
    public const int DefaultGsiPort = 43570;

    public const string DotaAppId = "570";
    public const string DotaProcessName = "dota2";
    public const string LaunchOption = "-gamestateintegration";

    public const string DeveloperPortalUrl = "https://discord.com/developers/applications";
    public const string HeroImageBaseUrl = "https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/";
    public static string DotaLogoUrl => DotaLogo.Url;
    public const string HeroListUrl = "https://www.dota2.com/datafeed/herolist?language=english";
    public const string DiscordAppInfoUrl = "https://discord.com/api/v10/applications/{0}/rpc";
    public const string OpenDotaPlayerUrl = "https://www.opendota.com/players/{0}";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string ExecutablePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>
    /// Application ID, встроенный при сборке (discord-app-id.txt). Используется всеми пользователями,
    /// если в настройках не указан свой. Пустая строка — ID не встроен.
    /// </summary>
    internal static string EmbeddedClientId { get; set; } = ReadEmbeddedClientId();

    static string ReadEmbeddedClientId()
    {
        var value = typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DiscordClientId")?.Value?.Trim();
        return AppSettings.IsValidClientId(value) ? value! : "";
    }
}

internal static class AppPaths
{
    // DOTA2RPC_HOME переопределяет все каталоги данных (используется тестами, чтобы не трогать настройки пользователя).
    static string? Home => Environment.GetEnvironmentVariable("DOTA2RPC_HOME");

    public static string SettingsDir => Home is { Length: > 0 } h
        ? Path.Combine(h, "settings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.ShortName);

    public static string LocalDir => Home is { Length: > 0 } h
        ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.ShortName);

    public static string SettingsFile => Path.Combine(SettingsDir, "settings.json");
    public static string LogDir => Path.Combine(LocalDir, "logs");
    public static string CacheDir => Path.Combine(LocalDir, "cache");
}
