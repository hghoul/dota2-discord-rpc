using System.Text;
using Dota2RPC.Core;
using Dota2RPC.Steam;

namespace Dota2RPC.Gsi;

internal enum GsiConfigState { DotaNotFound, Missing, Outdated, Installed }

internal sealed record GsiConfigStatus(GsiConfigState State, string? DotaPath, string? ConfigPath, string Message);

/// <summary>Файл game\dota\cfg\gamestate_integration\gamestate_integration_dota2rpc.cfg.</summary>
internal static class GsiConfig
{
    public static string ConfigDir(string dotaPath) =>
        Path.Combine(dotaPath, "game", "dota", "cfg", "gamestate_integration");

    public static string ConfigPath(string dotaPath) => Path.Combine(ConfigDir(dotaPath), AppInfo.GsiConfigFileName);

    public static string Render(int port, string token)
    {
        var nl = "\r\n";
        return string.Join(nl,
            $"\"{AppInfo.GsiConfigBlockName}\"",
            "{",
            $"\t\"uri\"\t\t\"http://127.0.0.1:{port}/\"",
            "\t\"timeout\"\t\"5.0\"",
            "\t\"buffer\"\t\"0.1\"",
            "\t\"throttle\"\t\"1.0\"", // не чаще раза в секунду: статус в Discord всё равно обновляется раз в 4 с
            "\t\"heartbeat\"\t\"30.0\"",
            "\t\"data\"",
            "\t{",
            "\t\t\"provider\"\t\"1\"",
            "\t\t\"map\"\t\t\"1\"",
            "\t\t\"player\"\t\"1\"",
            "\t\t\"hero\"\t\t\"1\"",
            "\t}",
            "\t\"auth\"",
            "\t{",
            $"\t\t\"token\"\t\t\"{token}\"",
            "\t}",
            "}",
            "");
    }

    public static GsiConfigStatus Check(string? configuredDotaPath, int port, string token)
    {
        var dota = SteamLocator.FindDotaPath(configuredDotaPath);
        if (dota is null)
            return new GsiConfigStatus(GsiConfigState.DotaNotFound, null, null, "Папка Dota 2 не найдена");

        var path = ConfigPath(dota);
        if (!File.Exists(path))
            return new GsiConfigStatus(GsiConfigState.Missing, dota, path, "Конфигурация GSI не установлена");

        try
        {
            var root = Vdf.Parse(File.ReadAllText(path, Encoding.UTF8));
            var block = root.Children?.FirstOrDefault(c => c.IsBlock);
            var uri = block?["uri"]?.Value;
            var fileToken = block?.Get("auth", "token")?.Value;
            var hasHero = block?.Get("data", "hero")?.Value == "1";
            if (uri != $"http://127.0.0.1:{port}/" || fileToken != token || !hasHero)
                return new GsiConfigStatus(GsiConfigState.Outdated, dota, path, "Конфигурация GSI устарела — нажмите «Установить / обновить»");
        }
        catch (Exception ex)
        {
            return new GsiConfigStatus(GsiConfigState.Outdated, dota, path, $"Конфигурация GSI повреждена: {ex.Message}");
        }

        return new GsiConfigStatus(GsiConfigState.Installed, dota, path, "Конфигурация GSI установлена");
    }

    /// <summary>Записывает конфиг. Бросает UnauthorizedAccessException, если нет прав на папку игры.</summary>
    public static string Install(string dotaPath, int port, string token)
    {
        if (!SteamLocator.IsDotaDir(dotaPath))
            throw new DirectoryNotFoundException($"«{dotaPath}» не похожа на папку Dota 2 (нет game\\dota)");

        Directory.CreateDirectory(ConfigDir(dotaPath));
        var path = ConfigPath(dotaPath);
        File.WriteAllText(path, Render(port, token), new UTF8Encoding(false));
        Log.Info($"GSI: конфигурация записана: {path}");
        return path;
    }

    public static bool Remove(string? dotaPath)
    {
        var dota = SteamLocator.FindDotaPath(dotaPath);
        if (dota is null) return false;
        var path = ConfigPath(dota);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        Log.Info($"GSI: конфигурация удалена: {path}");
        try
        {
            // Папку gamestate_integration создаём мы — если в ней больше ничего нет, убираем и её.
            var dir = ConfigDir(dota);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Log.Warn($"GSI: не удалось удалить пустую папку конфигурации: {ex.Message}");
        }
        return true;
    }
}
