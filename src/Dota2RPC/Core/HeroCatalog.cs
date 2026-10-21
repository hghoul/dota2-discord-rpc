using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Dota2RPC.Core;

internal sealed record HeroInfo(int Id, string Name, string DisplayName);

/// <summary>
/// Справочник героев. Встроенный список сгенерирован из официального фида Valve
/// (https://www.dota2.com/datafeed/herolist) скриптом tools\update-heroes.ps1.
/// При появлении неизвестного героя список обновляется из того же фида и кэшируется.
/// </summary>
internal sealed class HeroCatalog
{
    const string Prefix = "npc_dota_hero_";
    static readonly TimeSpan RefreshCooldown = TimeSpan.FromHours(1);

    readonly ConcurrentDictionary<string, HeroInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, bool> _imageOk = new(StringComparer.OrdinalIgnoreCase);
    readonly bool _allowNetwork;
    DateTime _lastRefreshAttemptUtc = DateTime.MinValue;
    int _refreshing;

    public HeroCatalog(bool allowNetwork = true)
    {
        _allowNetwork = allowNetwork;
        LoadEmbedded();
        LoadCache();
    }

    public int Count => _byName.Count;

    static string CachePath => Path.Combine(AppPaths.CacheDir, "heroes.json");

    public static string ShortName(string internalName) =>
        internalName.StartsWith(Prefix, StringComparison.Ordinal) ? internalName[Prefix.Length..] : internalName;

    public string DisplayName(string internalName)
    {
        var shortName = ShortName(internalName);
        if (_byName.TryGetValue(shortName, out var info)) return info.DisplayName;

        RequestRefresh();
        return Prettify(shortName);
    }

    /// <summary>URL иконки героя на CDN Valve. Если ранее выяснилось, что картинки нет, — логотип Dota 2.</summary>
    public string ImageUrl(string internalName, bool animated = false)
    {
        var shortName = ShortName(internalName);
        if (animated && HeroAnimations.TryGetUrl(shortName) is { } animation) return animation;
        if (_imageOk.TryGetValue(shortName, out var ok) && !ok) return AppInfo.DotaLogoUrl;
        if (!_imageOk.ContainsKey(shortName)) _ = VerifyImageAsync(shortName);
        return HeroImageUrl(shortName);
    }

    public static string HeroImageUrl(string shortName) => AppInfo.HeroImageBaseUrl + shortName + ".png";

    /// <summary>Запасной вариант имени: из внутреннего имени игры (shadow_shaman → Shadow Shaman).</summary>
    internal static string Prettify(string shortName) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(shortName.Replace('_', ' '));

    void LoadEmbedded()
    {
        try
        {
            using var stream = typeof(HeroCatalog).Assembly.GetManifestResourceStream("Dota2RPC.heroes.json");
            if (stream is null)
            {
                Log.Warn("Встроенный список героев не найден");
                return;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Merge(ParseStored(reader.ReadToEnd()));
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось загрузить встроенный список героев", ex);
        }
    }

    void LoadCache()
    {
        try
        {
            if (File.Exists(CachePath)) Merge(ParseStored(File.ReadAllText(CachePath, Encoding.UTF8)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Кэш героев повреждён: {ex.Message}");
        }
    }

    void Merge(IEnumerable<HeroInfo> heroes)
    {
        foreach (var h in heroes) _byName[ShortName(h.Name)] = h;
    }

    internal static List<HeroInfo> ParseStored(string json)
    {
        var list = new List<HeroInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("id").GetInt32();
            var name = e.GetProperty("name").GetString();
            var display = e.GetProperty("displayName").GetString();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(display)) list.Add(new HeroInfo(id, name, display));
        }
        return list;
    }

    /// <summary>Разбор ответа https://www.dota2.com/datafeed/herolist.</summary>
    internal static List<HeroInfo> ParseDatafeed(string json)
    {
        var list = new List<HeroInfo>();
        using var doc = JsonDocument.Parse(json);
        var heroes = doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("heroes");
        foreach (var e in heroes.EnumerateArray())
        {
            var id = e.GetProperty("id").GetInt32();
            var name = e.GetProperty("name").GetString();
            var display = e.TryGetProperty("name_english_loc", out var en) && en.GetString() is { Length: > 0 } s
                ? s
                : e.GetProperty("name_loc").GetString();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(display)) list.Add(new HeroInfo(id, ShortName(name), display));
        }
        return list;
    }

    public void RequestRefresh()
    {
        if (!_allowNetwork) return;
        if (DateTime.UtcNow - _lastRefreshAttemptUtc < RefreshCooldown) return;
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _lastRefreshAttemptUtc = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                var json = await Http.Client.GetStringAsync(AppInfo.HeroListUrl).ConfigureAwait(false);
                var heroes = ParseDatafeed(json);
                if (heroes.Count < 100) throw new InvalidDataException($"подозрительно мало героев: {heroes.Count}");
                Merge(heroes);
                Directory.CreateDirectory(AppPaths.CacheDir);
                var stored = heroes.Select(h => new { id = h.Id, name = h.Name, displayName = h.DisplayName });
                await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(stored), new UTF8Encoding(false)).ConfigureAwait(false);
                Log.Info($"Список героев обновлён из фида Valve: {heroes.Count}");
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось обновить список героев: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    async Task VerifyImageAsync(string shortName)
    {
        if (!_allowNetwork || !_imageOk.TryAdd(shortName, true)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, HeroImageUrl(shortName));
            using var response = await Http.Client.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _imageOk[shortName] = false;
                Log.Warn($"Иконка героя {shortName} отсутствует на CDN, будет показан логотип Dota 2");
            }
        }
        catch (Exception ex)
        {
            // Сетевые ошибки не считаем отсутствием картинки — Discord загрузит её сам.
            Log.Warn($"Не удалось проверить иконку {shortName}: {ex.Message}");
        }
    }
}

internal static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.ShortName}/{AppInfo.Version}");
        return client;
    }
}
