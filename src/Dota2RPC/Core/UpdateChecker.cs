using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dota2RPC.Core;

internal sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string SetupUrl, string? Sha256, string Notes);

/// <summary>
/// Проверка обновлений по последнему релизу на GitHub (публичный API, без ключей).
/// Обновление ставится только по кнопке пользователя: Setup.exe скачивается из релиза,
/// проверяется по SHA256 из описания релиза и запускается — установщик сам закрывает старую версию.
/// </summary>
internal static partial class UpdateChecker
{
    public const string Repository = "hghoul/dota2-discord-rpc";
    static string LatestApi => $"https://api.github.com/repos/{Repository}/releases/latest";

    public static Version CurrentVersion =>
        Version.TryParse(AppInfo.Version, out var v) ? v : new Version(0, 0, 0);

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.Client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // релизов ещё нет
        response.EnsureSuccessStatusCode();
        var info = ParseRelease(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return info is not null && info.Version > CurrentVersion ? info : null;
    }

    /// <summary>Разбор ответа /releases/latest. null — релиз без Setup.exe или с непонятной версией.</summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        string? setupUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), "Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            // Скачиваем только из релизов этого репозитория на github.com.
            if (url is not null && url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                setupUrl = url;
        }
        if (setupUrl is null) return null;

        var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var sha = Sha256Regex().Match(body) is { Success: true } m ? m.Value.ToUpperInvariant() : null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repository}/releases";
        return new UpdateInfo(version, tag, page, setupUrl, sha, body);
    }

    /// <summary>Скачивает Setup.exe во временную папку и проверяет контрольную сумму.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<int>? progress, CancellationToken ct = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Dota2RPC-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Dota2RPC-Setup-{update.Version}.exe");

        using (var response = await Http.Client.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        if (update.Sha256 is { } expected)
        {
            string actual;
            await using (var file = File.OpenRead(path)) // файл должен быть закрыт до возможного удаления
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                throw new InvalidDataException("Контрольная сумма скачанного установщика не совпала с указанной в релизе. Обновление отменено.");
            }
        }
        return path;
    }

    /// <summary>
    /// Запускает установщик в тихом режиме: он закроет эту программу, обновит файлы и запустит новую версию.
    /// </summary>
    public static void RunInstaller(string setupPath)
    {
        Log.Info($"Обновление: запуск {setupPath}");
        using (Process.Start(new ProcessStartInfo(setupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1")
               {
                   UseShellExecute = true,
               })) { }
    }

    [GeneratedRegex("\\b[0-9A-Fa-f]{64}\\b")]
    private static partial Regex Sha256Regex();
}
