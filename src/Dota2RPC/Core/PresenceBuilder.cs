using Dota2RPC.Discord;
using Dota2RPC.Gsi;

namespace Dota2RPC.Core;

internal sealed record PresenceInput(
    GsiSnapshot? Gsi,
    bool DotaRunning,
    DateTime? DotaStartedUtc,
    DateTime NowUtc,
    AppSettings Settings,
    bool GsiConfirmed = false);

/// <summary>
/// Превращает реальное состояние игры в статус Discord. Если какое-то значение не пришло из GSI,
/// соответствующая часть статуса просто не выводится.
/// </summary>
internal sealed class PresenceBuilder(HeroCatalog heroes)
{
    // Якорь времени: «начало» таймера не пересчитываем на каждую секунду, чтобы не дёргать Discord.
    string? _anchorKey;
    long _anchorValue;

    public static string TeamName(string? team) => team switch
    {
        "radiant" => "Силы Света",
        "dire" => "Силы Тьмы",
        _ => "",
    };

    public PresenceActivity? Build(PresenceInput input)
    {
        if (!input.DotaRunning) return null;

        var s = input.Gsi;
        var now = new DateTimeOffset(input.NowUtc).ToUnixTimeSeconds();
        long? sessionStart = input.DotaStartedUtc is { } st ? new DateTimeOffset(st).ToUnixTimeSeconds() : null;

        if (s is null || (!s.HasMap && !s.HasPlayer))
        {
            // Данных от игры в этом запуске ещё не было. В главном меню Dota присылает пакеты GSI только
            // при изменениях, поэтому тишина при уже проверенной интеграции означает главное меню.
            // Первые секунды после старта процесса игра ещё загружается.
            var startingUp = input.DotaStartedUtc is { } started && input.NowUtc - started < StartupGrace;
            if (!startingUp && input.GsiConfirmed) return Menu(null, input.Settings, sessionStart);
            return new PresenceActivity
            {
                Details = Ico(input.Settings, "🎮", startingUp ? "Dota 2 запускается" : "Dota 2 запущена"),
                LargeImage = AppInfo.DotaLogoUrl,
                LargeText = "Dota 2",
                StartUnix = sessionStart,
            };
        }

        if (!s.InMatch) return Menu(s, input.Settings, sessionStart);

        // Часы матча отсчитываем от момента получения пакета, а не от «сейчас»: тогда начало таймера
        // не сдвигается между пакетами и Discord не получает лишних обновлений.
        var at = new DateTimeOffset(s.ReceivedUtc).ToUnixTimeSeconds();
        if (s.IsSpectating) return BuildSpectating(s, at, input.Settings);
        if (s.IsHeroDemo) return BuildHeroDemo(s, input.Settings);

        var state = s.ShortGameState ?? "";
        return state switch
        {
            "GAME_IN_PROGRESS" => BuildInGame(s, at, input.Settings),
            "PRE_GAME" => BuildPreGame(s, at, input.Settings),
            "POST_GAME" => BuildPostGame(s, input.Settings),
            "HERO_SELECTION" or "PLAYER_DRAFT" => BuildLobby(s, "🎯", "Выбор героя", input.Settings),
            "STRATEGY_TIME" => BuildLobby(s, "🧠", "Стратегия перед матчем", input.Settings),
            "TEAM_SHOWCASE" => BuildLobby(s, "🎭", "Представление команд", input.Settings),
            "INIT" or "WAIT_FOR_PLAYERS_TO_LOAD" or "WAIT_FOR_MAP_TO_LOAD" => BuildLobby(s, "⌛", "Загрузка матча", input.Settings),
            "CUSTOM_GAME_SETUP" => BuildLobby(s, "🛠️", "Настройка пользовательской игры", input.Settings),
            "DISCONNECT" => BuildLobby(s, "📡", "Отключение от матча", input.Settings),
            _ => BuildLobby(s, "⚔️", "В матче", input.Settings),
        };
    }

    /// <summary>Сколько после запуска dota2.exe показывать «запускается», пока нет данных от игры.</summary>
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(25);

    PresenceActivity? Menu(GsiSnapshot? s, AppSettings o, long? sessionStart)
    {
        if (!o.ShowInMenu) return null;
        return new PresenceActivity
        {
            Details = Ico(o, "🏠", "В главном меню"),
            LargeImage = AppInfo.DotaLogoUrl,
            LargeText = "Dota 2",
            StartUnix = sessionStart,
            Buttons = s is null ? [] : ProfileButtons(s, o),
        };
    }

    PresenceActivity BuildInGame(GsiSnapshot s, long now, AppSettings o)
    {
        var heroName = s.HasHero ? heroes.DisplayName(s.HeroName!) : null;

        var details = heroName ?? Ico(o, "⚔️", "В матче");
        if (heroName is not null && o.ShowLevel && s.HeroLevel is > 0)
            details += " · " + Ico(o, "⭐", $"Ур. {s.HeroLevel}");

        var parts = new List<string>();
        if (s.Paused) parts.Add(o.ShowEmoji ? "⏸️ Пауза" : "⏸ Пауза");
        if (o.ShowKda && Kda(s, o) is { } kda) parts.Add(kda);
        if (o.ShowEmoji && o.ShowKda && s.KillStreak is >= 3) parts.Add($"🔥 {s.KillStreak}"); // серия убийств из GSI
        if (o.ShowGameTime && s.ClockTime is int clock && clock >= 0) parts.Add(Ico(o, "⏱️", $"{clock / 60} мин"));
        var stateText = parts.Count > 0 ? string.Join(" · ", parts) : Ico(o, "⚔️", "Матч идёт");

        long? start = null;
        if (o.ShowGameTime && !s.Paused && s.ClockTime is int c && c >= 0)
            start = Anchor($"{s.MatchId}:game", now - c);

        return new PresenceActivity
        {
            Details = details,
            State = stateText,
            LargeImage = heroName is not null ? heroes.ImageUrl(s.HeroName!, o.AnimatedHeroIcons) : AppInfo.DotaLogoUrl,
            LargeText = LargeText(heroName, s, o),
            SmallImage = heroName is not null ? AppInfo.DotaLogoUrl : null,
            SmallText = heroName is not null ? SmallText(s) : null,
            StartUnix = start,
            Buttons = ProfileButtons(s, o),
        };
    }

    PresenceActivity BuildPreGame(GsiSnapshot s, long now, AppSettings o)
    {
        var heroName = s.HasHero ? heroes.DisplayName(s.HeroName!) : null;
        var details = heroName ?? Ico(o, "⏳", "Подготовка к матчу");
        if (heroName is not null && o.ShowLevel && s.HeroLevel is > 0)
            details += " · " + Ico(o, "⭐", $"Ур. {s.HeroLevel}");

        long? end = null;
        if (!s.Paused && s.ClockTime is int c && c < 0)
            end = Anchor($"{s.MatchId}:pre", now - c);

        return new PresenceActivity
        {
            Details = details,
            State = s.Paused ? Ico(o, "⏸️", "Пауза · подготовка") : Ico(o, "⏳", "Подготовка к началу матча"),
            LargeImage = heroName is not null ? heroes.ImageUrl(s.HeroName!, o.AnimatedHeroIcons) : AppInfo.DotaLogoUrl,
            LargeText = LargeText(heroName, s, o),
            SmallImage = heroName is not null ? AppInfo.DotaLogoUrl : null,
            SmallText = heroName is not null ? SmallText(s) : null,
            EndUnix = end,
            Buttons = ProfileButtons(s, o),
        };
    }

    PresenceActivity BuildPostGame(GsiSnapshot s, AppSettings o)
    {
        var heroName = s.HasHero ? heroes.DisplayName(s.HeroName!) : null;
        var result = MatchResult(s);
        var details = (result, o.ShowEmoji) switch
        {
            (null, _) => Ico(o, "🏁", "Матч завершён"),
            ("Победа", true) => "🏆 Победа · матч завершён",
            (_, true) => "💔 Поражение · матч завершён",
            _ => $"Матч завершён · {result}",
        };

        var parts = new List<string>();
        if (heroName is not null) parts.Add(heroName);
        if (o.ShowKda && Kda(s, o) is { } kda) parts.Add(kda);
        if (o.ShowGameTime && s.ClockTime is int clock && clock > 0) parts.Add(Ico(o, "⏱️", $"{clock / 60} мин"));

        return new PresenceActivity
        {
            Details = details,
            State = parts.Count > 0 ? string.Join(" · ", parts) : null,
            LargeImage = heroName is not null ? heroes.ImageUrl(s.HeroName!, o.AnimatedHeroIcons) : AppInfo.DotaLogoUrl,
            LargeText = LargeText(heroName, s, o),
            SmallImage = heroName is not null ? AppInfo.DotaLogoUrl : null,
            SmallText = heroName is not null ? SmallText(s) : null,
            Buttons = ProfileButtons(s, o),
        };
    }

    PresenceActivity BuildLobby(GsiSnapshot s, string emoji, string title, AppSettings o)
    {
        var heroName = s.HasHero ? heroes.DisplayName(s.HeroName!) : null;
        string? state = heroName is not null ? Ico(o, "🦸", $"Герой: {heroName}") : null;
        if (!string.IsNullOrWhiteSpace(s.CustomGameName))
            state = state is null ? s.CustomGameName : $"{state} · {s.CustomGameName}";

        return new PresenceActivity
        {
            Details = Ico(o, emoji, title),
            State = state,
            LargeImage = heroName is not null ? heroes.ImageUrl(s.HeroName!, o.AnimatedHeroIcons) : AppInfo.DotaLogoUrl,
            LargeText = heroName ?? "Dota 2",
            SmallImage = heroName is not null ? AppInfo.DotaLogoUrl : null,
            SmallText = heroName is not null ? SmallText(s) : null,
            Buttons = ProfileButtons(s, o),
        };
    }

    PresenceActivity BuildHeroDemo(GsiSnapshot s, AppSettings o)
    {
        var heroName = s.HasHero ? heroes.DisplayName(s.HeroName!) : null;
        string? state = null;
        if (heroName is not null)
        {
            state = heroName;
            if (o.ShowLevel && s.HeroLevel is > 0) state += " · " + Ico(o, "⭐", $"Ур. {s.HeroLevel}");
        }

        return new PresenceActivity
        {
            Details = Ico(o, "🧪", "Пробует героя"),
            State = state,
            LargeImage = heroName is not null ? heroes.ImageUrl(s.HeroName!, o.AnimatedHeroIcons) : AppInfo.DotaLogoUrl,
            LargeText = heroName is null ? "Демо героя" : $"{heroName} · демо героя",
            SmallImage = heroName is not null ? AppInfo.DotaLogoUrl : null,
            SmallText = heroName is not null ? "Dota 2 · демо героя" : null,
        };
    }

    PresenceActivity BuildSpectating(GsiSnapshot s, long now, AppSettings o)
    {
        var parts = new List<string>();
        if (s.Paused) parts.Add(o.ShowEmoji ? "⏸️ Пауза" : "⏸ Пауза");
        if (s.RadiantScore is { } r && s.DireScore is { } d) parts.Add(o.ShowEmoji ? $"🟢 Свет {r} : {d} Тьма 🔴" : $"Свет {r} : {d} Тьма");
        if (o.ShowGameTime && s.ClockTime is int clock && clock >= 0) parts.Add(Ico(o, "⏱️", $"{clock / 60} мин"));

        long? start = null;
        if (o.ShowGameTime && !s.Paused && s.ShortGameState == "GAME_IN_PROGRESS" && s.ClockTime is int c && c >= 0)
            start = Anchor($"{s.MatchId}:spec", now - c);

        return new PresenceActivity
        {
            Details = Ico(o, "👀", "Наблюдает за матчем"),
            State = parts.Count > 0 ? string.Join(" · ", parts) : null,
            LargeImage = AppInfo.DotaLogoUrl,
            LargeText = "Dota 2 · режим наблюдателя",
            StartUnix = start,
        };
    }

    static string? Kda(GsiSnapshot s, AppSettings o) =>
        s.Kills is { } k && s.Deaths is { } d && s.Assists is { } a
            ? o.ShowEmoji ? $"🗡️ {k} · 💀 {d} · 🤝 {a}" : $"KDA {k} / {d} / {a}"
            : null;

    /// <summary>Эмодзи перед текстом, если в настройках включено цветное оформление.</summary>
    static string Ico(AppSettings o, string emoji, string text) => o.ShowEmoji ? $"{emoji} {text}" : text;

    internal static string? MatchResult(GsiSnapshot s)
    {
        if (string.IsNullOrEmpty(s.Team) || s.WinTeam is null or "" or "none") return null;
        return string.Equals(s.Team, s.WinTeam, StringComparison.OrdinalIgnoreCase) ? "Победа" : "Поражение";
    }

    static string LargeText(string? heroName, GsiSnapshot s, AppSettings o)
    {
        var parts = new List<string> { heroName ?? "Dota 2" };
        if (o.ShowTeamAndScore)
        {
            if (TeamName(s.Team) is { Length: > 0 } team)
                parts.Add(o.ShowEmoji ? (s.Team == "radiant" ? "🟢 " : "🔴 ") + team : team);
            if (s.RadiantScore is { } r && s.DireScore is { } d) parts.Add($"Счёт {r} : {d}");
        }
        if (o.ShowNetStats)
        {
            if (s.LastHits is { } lh && s.Denies is { } dn) parts.Add($"LH/DN {lh}/{dn}");
            if (s.Gpm is { } gpm) parts.Add($"GPM {gpm}");
            if (s.Xpm is { } xpm) parts.Add($"XPM {xpm}");
        }
        return string.Join(" · ", parts);
    }

    static string SmallText(GsiSnapshot s) =>
        string.IsNullOrWhiteSpace(s.CustomGameName) ? "Dota 2" : $"Dota 2 · {s.CustomGameName}";

    static IReadOnlyList<PresenceButton> ProfileButtons(GsiSnapshot s, AppSettings o) =>
        o.ShowProfileButton && s.AccountId32 is { } id
            ? [new PresenceButton("Профиль на Dotabuff", string.Format(AppInfo.DotabuffPlayerUrl, id))]
            : [];

    long Anchor(string key, long desired)
    {
        if (_anchorKey != key || Math.Abs(_anchorValue - desired) > 2)
        {
            _anchorKey = key;
            _anchorValue = desired;
        }
        return _anchorValue;
    }
}
