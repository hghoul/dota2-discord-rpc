using System.Text.Json;

namespace Dota2RPC.Core;

/// <summary>
/// Красный логотип Dota 2 — официальная иконка игры из каталога Discord (приложение Dota 2,
/// id 356875988589740042). Белый символ с CDN Valve на тёмном фоне Discord выглядит блекло.
/// Хэш картинки берётся из публичного /applications/{id}/rpc, чтобы ссылка не устаревала.
/// </summary>
internal static class DotaLogo
{
    public const string DiscordDotaAppId = "356875988589740042";
    const string KnownIconHash = "6b4b3fa4c83555d3008de69d33a60588";

    public static string Url { get; private set; } = IconUrl(KnownIconHash);

    static string IconUrl(string hash) => $"https://cdn.discordapp.com/app-icons/{DiscordDotaAppId}/{hash}.png?size=512";

    /// <returns>true, если ссылка на иконку изменилась.</returns>
    public static async Task<bool> RefreshAsync()
    {
        try
        {
            var json = await Http.Client.GetStringAsync(string.Format(AppInfo.DiscordAppInfoUrl, DiscordDotaAppId)).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("icon", out var icon) &&
                icon.GetString() is { Length: >= 16 } hash && hash.All(char.IsAsciiHexDigit))
            {
                var url = IconUrl(hash);
                if (url == Url) return false;
                Url = url;
                Log.Info($"Логотип Dota 2 обновлён: {url}");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось уточнить логотип Dota 2, используется известная ссылка: {ex.Message}");
        }
        return false;
    }
}
