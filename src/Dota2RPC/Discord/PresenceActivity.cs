using System.Text.Json.Nodes;

namespace Dota2RPC.Discord;

internal sealed record PresenceButton(string Label, string Url);

/// <summary>Модель статуса Rich Presence (поля объекта activity команды SET_ACTIVITY).</summary>
internal sealed class PresenceActivity
{
    public required string Details { get; init; }
    public string? State { get; init; }
    public string? LargeImage { get; init; }
    public string? LargeText { get; init; }
    public string? SmallImage { get; init; }
    public string? SmallText { get; init; }

    /// <summary>Unix-время (секунды) начала — Discord сам показывает «прошло мм:сс» и обновляет счётчик.</summary>
    public long? StartUnix { get; init; }

    /// <summary>Unix-время (секунды) окончания — Discord показывает обратный отсчёт.</summary>
    public long? EndUnix { get; init; }

    public IReadOnlyList<PresenceButton> Buttons { get; init; } = [];

    public JsonObject ToJson()
    {
        var activity = new JsonObject
        {
            ["type"] = 0, // «Играет в …»
            ["details"] = Fit(Details),
        };
        if (!string.IsNullOrWhiteSpace(State)) activity["state"] = Fit(State);

        if (StartUnix is not null || EndUnix is not null)
        {
            var timestamps = new JsonObject();
            if (StartUnix is { } start) timestamps["start"] = start;
            if (EndUnix is { } end) timestamps["end"] = end;
            activity["timestamps"] = timestamps;
        }

        var assets = new JsonObject();
        if (!string.IsNullOrEmpty(LargeImage)) assets["large_image"] = LargeImage;
        if (!string.IsNullOrWhiteSpace(LargeText)) assets["large_text"] = Fit(LargeText);
        if (!string.IsNullOrEmpty(SmallImage)) assets["small_image"] = SmallImage;
        if (!string.IsNullOrWhiteSpace(SmallText)) assets["small_text"] = Fit(SmallText);
        if (assets.Count > 0) activity["assets"] = assets;

        if (Buttons.Count > 0)
        {
            var buttons = new JsonArray();
            foreach (var b in Buttons.Take(2))
                buttons.Add(new JsonObject { ["label"] = Truncate(b.Label, 32), ["url"] = Truncate(b.Url, 512) });
            activity["buttons"] = buttons;
        }

        activity["instance"] = false;
        return activity;
    }

    /// <summary>Строковое представление для сравнения: отправляем в Discord только реальные изменения.</summary>
    public string Signature => ToJson().ToJsonString();

    /// <summary>Discord требует от текстовых полей длину 2–128 символов.</summary>
    internal static string Fit(string value)
    {
        value = value.Trim();
        if (value.Length < 2) value = value.PadRight(2, '⠀');
        return Truncate(value, 128);
    }

    static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
