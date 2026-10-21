using System.Text;
using Dota2RPC.Core;

namespace Dota2RPC.Gsi;

/// <summary>
/// Режим разработчика: сохраняет по одному образцу пакета GSI на каждое сочетание «карта + этап матча + герой».
/// Файлы только локальные: %LOCALAPPDATA%\Dota2RPC\gsi-samples.
/// </summary>
internal static class GsiRecorder
{
    const int MaxFiles = 60;
    static readonly object Gate = new();
    static readonly HashSet<string> Saved = [];

    public static string Folder => Path.Combine(AppPaths.LocalDir, "gsi-samples");

    public static void Save(GsiSnapshot s)
    {
        if (s.RawJson is null) return;
        var key = string.Join("_",
            Clean(s.MapName ?? "nomap"),
            Clean(s.ShortGameState ?? (s.HasPlayer ? s.Activity ?? "player" : "empty")),
            s.IsSpectating ? "spectating" : "",
            s.HasHero ? Clean(HeroCatalog.ShortName(s.HeroName!)) : "")
            .Trim('_');

        lock (Gate)
        {
            if (!Saved.Add(key) || Saved.Count > MaxFiles) return;
        }
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd-HHmmss}_{key}.json"), s.RawJson, new UTF8Encoding(false));
            Log.Info($"GSI: сохранён образец пакета {key}");
        }
        catch (Exception ex)
        {
            Log.Warn($"GSI: не удалось сохранить образец: {ex.Message}");
        }
    }

    static string Clean(string value) =>
        new(value.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').Take(40).ToArray());
}
