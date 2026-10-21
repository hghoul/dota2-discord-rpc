using System.Text.Json.Nodes;
using Dota2RPC.Core;
using Dota2RPC.Discord;
using Dota2RPC.Gsi;
using Xunit;

namespace Dota2RPC.Tests;

public class GsiAndPresenceTests
{
    static readonly DateTime Now = new(2026, 10, 21, 18, 0, 0, DateTimeKind.Utc);
    static readonly long NowUnix = new DateTimeOffset(Now).ToUnixTimeSeconds();
    readonly HeroCatalog _heroes = new(allowNetwork: false);

    static GsiSnapshot Parse(string json) => GsiParser.Parse(json, Now);

    PresenceActivity? Build(GsiSnapshot? s, AppSettings? settings = null, bool running = true, PresenceBuilder? builder = null) =>
        (builder ?? new PresenceBuilder(_heroes)).Build(new PresenceInput(s, running, Now.AddMinutes(-30), Now, settings ?? TestEnv.Settings()));

    [Fact]
    public void Parses_in_game_payload()
    {
        var s = Parse(TestEnv.Fixture("ingame.json"));
        Assert.True(s.HasAuth);
        Assert.Equal("0123456789abcdef0123456789abcdef", s.AuthToken);
        Assert.True(s.InMatch);
        Assert.False(s.IsSpectating);
        Assert.Equal("GAME_IN_PROGRESS", s.ShortGameState);
        Assert.Equal(1385, s.ClockTime);
        Assert.Equal("npc_dota_hero_pudge", s.HeroName);
        Assert.Equal(18, s.HeroLevel);
        Assert.Equal((7, 2, 11), (s.Kills, s.Deaths, s.Assists));
        Assert.Equal("radiant", s.Team);
        Assert.Equal((21, 14), (s.RadiantScore, s.DireScore));
        Assert.Equal("39734273", s.AccountId32);
    }

    [Fact]
    public void In_game_presence_shows_hero_level_kda_and_minutes()
    {
        var a = Build(Parse(TestEnv.Fixture("ingame.json")))!;
        Assert.Equal("Pudge · Ур. 18", a.Details);
        Assert.Equal("KDA 7 / 2 / 11 · 23 мин", a.State);
        Assert.Equal("https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/pudge.png", a.LargeImage);
        Assert.Equal(AppInfo.DotaLogoUrl, a.SmallImage);
        Assert.Equal("Pudge · Силы Света · Счёт 21 : 14", a.LargeText);
        Assert.Equal(NowUnix - 1385, a.StartUnix);
        Assert.Null(a.EndUnix);
        Assert.Empty(a.Buttons);
    }

    [Fact]
    public void Display_options_are_respected()
    {
        var settings = TestEnv.Settings(o =>
        {
            o.ShowLevel = false;
            o.ShowKda = false;
            o.ShowGameTime = false;
            o.ShowTeamAndScore = false;
            o.ShowNetStats = true;
            o.ShowProfileButton = true;
        });
        var a = Build(Parse(TestEnv.Fixture("ingame.json")), settings)!;
        Assert.Equal("Pudge", a.Details);
        Assert.Equal("Матч идёт", a.State);
        Assert.Null(a.StartUnix);
        Assert.Equal("Pudge · LH/DN 182/9 · GPM 545 · XPM 610", a.LargeText);
        var button = Assert.Single(a.Buttons);
        Assert.Equal("https://www.opendota.com/players/39734273", button.Url);
    }

    [Fact]
    public void Missing_values_are_omitted_not_invented()
    {
        var json = JsonNode.Parse(TestEnv.Fixture("ingame.json"))!.AsObject();
        json["hero"]!.AsObject().Remove("level");
        json["player"]!.AsObject().Remove("deaths");
        var a = Build(Parse(json.ToJsonString()))!;
        Assert.Equal("Pudge", a.Details);          // уровня нет — не выводим
        Assert.Equal("23 мин", a.State);            // K/D/A неполный — не выводим
    }

    [Fact]
    public void Paused_match_has_no_running_timer()
    {
        var json = JsonNode.Parse(TestEnv.Fixture("ingame.json"))!.AsObject();
        json["map"]!["paused"] = true;
        var a = Build(Parse(json.ToJsonString()))!;
        Assert.StartsWith("⏸ Пауза", a.State);
        Assert.Null(a.StartUnix);
    }

    [Fact]
    public void Timer_anchor_is_stable_between_ticks()
    {
        var builder = new PresenceBuilder(_heroes);
        var json = JsonNode.Parse(TestEnv.Fixture("ingame.json"))!.AsObject();
        var first = Build(Parse(json.ToJsonString()), builder: builder)!;
        json["map"]!["clock_time"] = 1386; // часы игры ушли на секунду вперёд, а «сейчас» — то же
        var second = Build(Parse(json.ToJsonString()), builder: builder)!;
        Assert.Equal(first.StartUnix, second.StartUnix);
        json["map"]!["clock_time"] = 1300; // большой скачок (переподключение) — якорь пересчитывается
        var third = Build(Parse(json.ToJsonString()), builder: builder)!;
        Assert.Equal(NowUnix - 1300, third.StartUnix);
    }

    [Fact]
    public void Pre_game_shows_countdown()
    {
        var json = JsonNode.Parse(TestEnv.Fixture("ingame.json"))!.AsObject();
        json["map"]!["game_state"] = "DOTA_GAMERULES_STATE_PRE_GAME";
        json["map"]!["clock_time"] = -45;
        json["hero"]!["level"] = 1;
        var a = Build(Parse(json.ToJsonString()))!;
        Assert.Equal("Pudge · Ур. 1", a.Details);
        Assert.Equal(NowUnix + 45, a.EndUnix);
        Assert.Null(a.StartUnix);
    }

    [Theory]
    [InlineData("radiant", "Победа")]
    [InlineData("dire", "Поражение")]
    public void Post_game_shows_result(string winner, string expected)
    {
        var json = JsonNode.Parse(TestEnv.Fixture("ingame.json"))!.AsObject();
        json["map"]!["game_state"] = "DOTA_GAMERULES_STATE_POST_GAME";
        json["map"]!["win_team"] = winner;
        var a = Build(Parse(json.ToJsonString()))!;
        Assert.Equal($"Матч завершён · {expected}", a.Details);
        Assert.Equal("Pudge · KDA 7 / 2 / 11 · 23 мин", a.State);
        Assert.Null(a.StartUnix);
    }

    [Fact]
    public void Hero_selection_without_pick()
    {
        var json = """
            {"provider":{"name":"Dota 2","appid":570},
             "map":{"name":"start","matchid":"8012345679","game_state":"DOTA_GAMERULES_STATE_HERO_SELECTION","clock_time":-80,"paused":false},
             "player":{"steamid":"76561198000000001","activity":"playing","kills":0,"deaths":0,"assists":0,"team_name":"dire"},
             "hero":{"id":0}}
            """;
        var a = Build(Parse(json))!;
        Assert.Equal("Выбор героя", a.Details);
        Assert.Null(a.State);
        Assert.Equal(AppInfo.DotaLogoUrl, a.LargeImage);
    }

    [Fact]
    public void Spectating_does_not_attribute_other_players_stats()
    {
        var s = Parse(TestEnv.Fixture("spectating.json"));
        Assert.True(s.IsSpectating);
        Assert.Null(s.HeroName);
        Assert.Null(s.Kills);
        var a = Build(s)!;
        Assert.Equal("Наблюдает за матчем", a.Details);
        Assert.Equal("Свет 5 : 8 Тьма · 10 мин", a.State);
    }

    [Fact]
    public void Menu_and_no_data_states()
    {
        var menu = Parse("""{"provider":{"name":"Dota 2","appid":570},"player":{"steamid":"76561198000000001","name":"x","activity":"menu"}}""");
        var a = Build(menu)!;
        Assert.Equal("В главном меню", a.Details);
        Assert.Equal(NowUnix - 1800, a.StartUnix); // время с запуска dota2.exe

        Assert.Null(Build(menu, TestEnv.Settings(o => o.ShowInMenu = false)));

        var noData = Build(null)!;
        Assert.Equal("Dota 2 запущена", noData.Details);

        Assert.Null(Build(Parse(TestEnv.Fixture("ingame.json")), running: false));
    }

    [Fact]
    public void Colorful_style_uses_emoji_and_red_logo()
    {
        var colorful = TestEnv.Settings(o => o.ShowEmoji = true);
        var json = JsonNode.Parse(TestEnv.Fixture("ingame.json"))!.AsObject();
        json["player"]!["kill_streak"] = 4;
        var a = Build(Parse(json.ToJsonString()), colorful)!;
        Assert.Equal("Pudge · ⭐ Ур. 18", a.Details);
        Assert.Equal("🗡️ 7 · 💀 2 · 🤝 11 · 🔥 4 · ⏱️ 23 мин", a.State);
        Assert.Equal("Pudge · 🟢 Силы Света · Счёт 21 : 14", a.LargeText);
        Assert.StartsWith("https://cdn.discordapp.com/app-icons/356875988589740042/", a.SmallImage);

        var menu = Build(Parse("""{"provider":{"name":"Dota 2"},"player":{"activity":"menu"}}"""), colorful)!;
        Assert.Equal("🏠 В главном меню", menu.Details);
        Assert.Equal(AppInfo.DotaLogoUrl, menu.LargeImage); // красный логотип в главном меню

        json["map"]!["game_state"] = "DOTA_GAMERULES_STATE_POST_GAME";
        json["map"]!["win_team"] = "radiant";
        Assert.Equal("🏆 Победа · матч завершён", Build(Parse(json.ToJsonString()), colorful)!.Details);
    }

    [Fact]
    public void Activity_json_matches_discord_limits()
    {
        var a = new PresenceActivity
        {
            Details = "x",
            State = new string('a', 300),
            LargeImage = "https://example.invalid/a.png",
            Buttons = [new PresenceButton(new string('b', 40), "https://example.invalid")],
        };
        var json = a.ToJson();
        Assert.Equal(2, ((string)json["details"]!).Length);
        Assert.Equal(128, ((string)json["state"]!).Length);
        Assert.Equal(32, ((string)json["buttons"]![0]!["label"]!).Length);
        Assert.Equal(0, (int)json["type"]!);
        Assert.Null(json["timestamps"]);
    }

    [Fact]
    public void Unknown_hero_name_falls_back_to_game_name()
    {
        Assert.Equal("Shadow Shaman", HeroCatalog.Prettify("shadow_shaman"));
        Assert.True(_heroes.Count >= 120);
        Assert.Equal("Shadow Fiend", _heroes.DisplayName("npc_dota_hero_nevermore"));
        Assert.Equal("Outworld Destroyer", _heroes.DisplayName("npc_dota_hero_obsidian_destroyer"));
    }
}
