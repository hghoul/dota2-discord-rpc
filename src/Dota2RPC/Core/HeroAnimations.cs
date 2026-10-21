using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Dota2RPC.Core;

/// <summary>
/// Анимированные иконки героев (WebP), опубликованные в репозитории на GitHub и раздаваемые через jsDelivr.
/// Адрес вшивается при сборке (hero-animations-url.txt). Список доступных героев берётся из manifest.json:
/// для героя без анимации используется обычная статичная иконка Valve.
/// </summary>
internal static class HeroAnimations
{
    static readonly object Gate = new();
    static HashSet<string> _available = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>База вида https://cdn.jsdelivr.net/gh/owner/repo@v1/ (со слэшем на конце) или пустая строка.</summary>
    internal static string BaseUrl { get; set; } = ReadBaseUrl();

    public static bool Configured => BaseUrl.Length > 0;

    static string CachePath => Path.Combine(AppPaths.CacheDir, "hero-animations.json");

    static string ReadBaseUrl()
    {
        var value = typeof(HeroAnimations).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "HeroAnimationsBaseUrl")?.Value?.Trim();
        if (string.IsNullOrEmpty(value) || !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "";
        return value.EndsWith('/') ? value : value + "/";
    }

    /// <summary>URL анимации героя, если она опубликована.</summary>
    public static string? TryGetUrl(string shortName)
    {
        if (!Configured) return null;
        lock (Gate)
        {
            return _available.Contains(shortName) ? $"{BaseUrl}heroes/{shortName}.webp" : null;
        }
    }

    internal static void SetAvailable(IEnumerable<string> names)
    {
        lock (Gate) _available = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    internal static List<string> ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("heroes").EnumerateArray()
            .Select(e => e.GetString())
            .Where(n => !string.IsNullOrWhiteSpace(n) && n!.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            .Select(n => n!)
            .ToList();
    }

    /// <summary>Загружает список анимаций: сначала из кэша (для запуска без сети), затем свежий из репозитория.</summary>
    public static async Task<bool> LoadAsync()
    {
        if (!Configured) return false;
        try
        {
            if (File.Exists(CachePath)) SetAvailable(ParseManifest(await File.ReadAllTextAsync(CachePath, Encoding.UTF8).ConfigureAwait(false)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Кэш анимаций героев повреждён: {ex.Message}");
        }

        try
        {
            var json = await Http.Client.GetStringAsync(BaseUrl + "manifest.json").ConfigureAwait(false);
            var names = ParseManifest(json);
            SetAvailable(names);
            Directory.CreateDirectory(AppPaths.CacheDir);
            await File.WriteAllTextAsync(CachePath, json, new UTF8Encoding(false)).ConfigureAwait(false);
            Log.Info($"Анимированные иконки: доступно {names.Count} героев ({BaseUrl})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось загрузить список анимированных иконок, используются статичные/кэш: {ex.Message}");
            return false;
        }
    }
}
