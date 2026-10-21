using System.Text.Json;

namespace Dota2RPC.Gsi;

/// <summary>
/// Разобранное состояние из одного POST-запроса Dota 2 Game State Integration.
/// Все поля необязательны: Dota присылает только те узлы, которые доступны в текущем режиме
/// (главное меню, выбор героя, матч, наблюдение). Отсутствующие значения остаются null — ничего не придумываем.
/// </summary>
internal sealed class GsiSnapshot
{
    public DateTime ReceivedUtc { get; init; }
    public string? AuthToken { get; init; }
    public bool HasAuth { get; init; }

    // provider
    public long? ProviderTimestamp { get; init; }

    // map
    public bool HasMap { get; init; }
    public string? MapName { get; init; }
    public string? MatchId { get; init; }
    public int? GameTime { get; init; }
    public int? ClockTime { get; init; }
    public string? GameState { get; init; }
    public bool Paused { get; init; }
    public string? WinTeam { get; init; }
    public string? CustomGameName { get; init; }
    public int? RadiantScore { get; init; }
    public int? DireScore { get; init; }

    // player
    public bool HasPlayer { get; init; }
    public bool IsSpectating { get; init; }
    public string? Activity { get; init; }
    public string? PlayerName { get; init; }
    public string? SteamId { get; init; }
    public string? AccountId { get; init; }
    public string? Team { get; init; }
    public int? Kills { get; init; }
    public int? Deaths { get; init; }
    public int? Assists { get; init; }
    public int? KillStreak { get; init; }
    public int? LastHits { get; init; }
    public int? Denies { get; init; }
    public int? Gpm { get; init; }
    public int? Xpm { get; init; }

    // hero
    public int? HeroId { get; init; }
    public string? HeroName { get; init; }
    public int? HeroLevel { get; init; }
    public bool? HeroAlive { get; init; }
    public int? RespawnSeconds { get; init; }

    public bool HasHero => !string.IsNullOrEmpty(HeroName) && HeroName.StartsWith("npc_dota_hero_", StringComparison.Ordinal);

    /// <summary>Короткое имя состояния без префикса DOTA_GAMERULES_STATE_.</summary>
    public string? ShortGameState =>
        GameState is null ? null
        : GameState.StartsWith("DOTA_GAMERULES_STATE_", StringComparison.Ordinal) ? GameState["DOTA_GAMERULES_STATE_".Length..]
        : GameState;

    public bool InMatch => HasMap && !string.IsNullOrEmpty(GameState);

    /// <summary>32-битный Steam Account ID (для ссылок на профили), если его можно получить из данных игры.</summary>
    public string? AccountId32
    {
        get
        {
            if (!string.IsNullOrEmpty(AccountId) && AccountId.All(char.IsAsciiDigit) && AccountId != "0") return AccountId;
            if (ulong.TryParse(SteamId, out var id64) && id64 > 76561197960265728UL)
                return (id64 - 76561197960265728UL).ToString();
            return null;
        }
    }
}

internal static class GsiParser
{
    public static GsiSnapshot Parse(string json, DateTime receivedUtc)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, MaxDepth = 64 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Корневой элемент GSI не является объектом");

        var auth = Obj(root, "auth");
        var provider = Obj(root, "provider");
        var map = Obj(root, "map");
        var player = Obj(root, "player");
        var hero = Obj(root, "hero");

        // В режиме наблюдателя узлы player/hero содержат команды: {"team2": {"player0": {...}}}.
        bool spectating = player is { } p && (Obj(p, "team2") is not null || Obj(p, "team3") is not null);
        if (spectating) hero = null;

        return new GsiSnapshot
        {
            ReceivedUtc = receivedUtc,
            HasAuth = auth is not null,
            AuthToken = Str(auth, "token"),
            ProviderTimestamp = Long(provider, "timestamp"),

            HasMap = map is not null,
            MapName = Str(map, "name"),
            MatchId = Str(map, "matchid"),
            GameTime = Int(map, "game_time"),
            ClockTime = Int(map, "clock_time"),
            GameState = Str(map, "game_state"),
            Paused = Bool(map, "paused") ?? false,
            WinTeam = Str(map, "win_team"),
            CustomGameName = Str(map, "customgamename"),
            RadiantScore = Int(map, "radiant_score"),
            DireScore = Int(map, "dire_score"),

            HasPlayer = player is not null,
            IsSpectating = spectating,
            Activity = spectating ? null : Str(player, "activity"),
            PlayerName = spectating ? null : Str(player, "name"),
            SteamId = spectating ? null : Str(player, "steamid"),
            AccountId = spectating ? null : Str(player, "accountid"),
            Team = spectating ? null : Str(player, "team_name"),
            Kills = spectating ? null : Int(player, "kills"),
            Deaths = spectating ? null : Int(player, "deaths"),
            Assists = spectating ? null : Int(player, "assists"),
            KillStreak = spectating ? null : Int(player, "kill_streak"),
            LastHits = spectating ? null : Int(player, "last_hits"),
            Denies = spectating ? null : Int(player, "denies"),
            Gpm = spectating ? null : Int(player, "gpm"),
            Xpm = spectating ? null : Int(player, "xpm"),

            HeroId = Int(hero, "id"),
            HeroName = Str(hero, "name"),
            HeroLevel = Int(hero, "level"),
            HeroAlive = Bool(hero, "alive"),
            RespawnSeconds = Int(hero, "respawn_seconds"),
        };
    }

    static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    static JsonElement? Prop(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) ? v : null;

    static string? Str(JsonElement? parent, string name) => Prop(parent, name) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    static long? Long(JsonElement? parent, string name)
    {
        var v = Prop(parent, name);
        if (v is { ValueKind: JsonValueKind.Number } n)
        {
            if (n.TryGetInt64(out var l)) return l;
            if (n.TryGetDouble(out var d) && !double.IsNaN(d)) return (long)Math.Floor(d);
        }
        if (v is { ValueKind: JsonValueKind.String } s && long.TryParse(s.GetString(), out var parsed)) return parsed;
        return null;
    }

    static int? Int(JsonElement? parent, string name) =>
        Long(parent, name) is { } l ? (int)Math.Clamp(l, int.MinValue, int.MaxValue) : null;

    static bool? Bool(JsonElement? parent, string name) => Prop(parent, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.Number } v => v.TryGetInt32(out var i) ? i != 0 : null,
        { ValueKind: JsonValueKind.String } v => v.GetString() is "1" or "true" ? true : v.GetString() is "0" or "false" ? false : null,
        _ => null,
    };
}
