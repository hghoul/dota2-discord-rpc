using System.Text;
using Dota2RPC.Core;
using Dota2RPC.Gsi;
using Dota2RPC.Steam;
using Xunit;

namespace Dota2RPC.Tests;

public class SteamTests
{
    const string LocalConfig =
        "\"UserLocalConfigStore\"\n{\n\t\"friends\"\n\t{\n\t\t\"PersonaName\"\t\t\"Tester\"\n\t}\n" +
        "\t\"Software\"\n\t{\n\t\t\"Valve\"\n\t\t{\n\t\t\t\"Steam\"\n\t\t\t{\n" +
        "\t\t\t\t\"apps\"\n\t\t\t\t{\n" +
        "\t\t\t\t\t\"440\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1\"\n\t\t\t\t\t}\n" +
        "\t\t\t\t\t\"570\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1792540000\"\n__OPTIONS__\t\t\t\t\t}\n" +
        "\t\t\t\t}\n\t\t\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n" +
        "\t\t\t}\n\t\t}\n\t}\n}\n";

    static string Config(string? options) =>
        LocalConfig.Replace("__OPTIONS__", options is null ? "" : $"\t\t\t\t\t\t\"LaunchOptions\"\t\t\"{options}\"\n");

    static string? ReadOptions(string text) =>
        Vdf.Parse(text).Get("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "570", "LaunchOptions")?.Value;

    [Fact]
    public void Parses_escapes_and_nested_blocks()
    {
        var root = Vdf.Parse(Config(null));
        Assert.Equal("Tester", root.Get("UserLocalConfigStore", "friends", "PersonaName")?.Value);
        Assert.Equal(@"C:\Program Files (x86)\Steam", root.Get("UserLocalConfigStore", "Software", "Valve", "Steam", "path")?.Value);
    }

    [Fact]
    public void Adds_flag_when_launch_options_missing()
    {
        var original = Config(null);
        var updated = LaunchOptions.AddFlag(original);
        Assert.Equal("-gamestateintegration", ReadOptions(updated));
        Assert.Contains("\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-gamestateintegration\"\n\t\t\t\t\t}", updated);
        Assert.Equal(original, updated.Replace("\t\t\t\t\t\t\"LaunchOptions\"\t\t\"-gamestateintegration\"\n", ""));
    }

    [Fact]
    public void Appends_flag_to_existing_options_and_keeps_rest()
    {
        var original = Config("-novid -high +fps_max 240");
        var updated = LaunchOptions.AddFlag(original);
        Assert.Equal("-novid -high +fps_max 240 -gamestateintegration", ReadOptions(updated));
        Assert.Equal(original.Length + " -gamestateintegration".Length, updated.Length);
    }

    [Fact]
    public void Does_nothing_when_flag_present()
    {
        var original = Config("-novid -GameStateIntegration");
        Assert.Same(original, LaunchOptions.AddFlag(original));
    }

    [Fact]
    public void Creates_app_block_when_dota_never_configured()
    {
        var original = Config(null).Replace("\t\t\t\t\t\"570\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"LastPlayed\"\t\t\"1792540000\"\n\t\t\t\t\t}\n", "");
        Assert.Null(Vdf.Parse(original).Get("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "570"));
        var updated = LaunchOptions.AddFlag(original);
        Assert.Equal("-gamestateintegration", ReadOptions(updated));
        Assert.Equal("1", Vdf.Parse(updated).Get("UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "440", "LastPlayed")?.Value);
    }

    [Fact]
    public void Handles_crlf_and_quotes_in_options()
    {
        var original = Config("+exec \\\"autoexec.cfg\\\"").Replace("\n", "\r\n");
        var updated = LaunchOptions.AddFlag(original);
        Assert.Equal("+exec \"autoexec.cfg\" -gamestateintegration", ReadOptions(updated));
        Assert.DoesNotContain("\r\r", updated);
    }

    [Fact]
    public void Gsi_config_is_valid_vdf_with_token_and_port()
    {
        var text = GsiConfig.Render(43570, "0123456789abcdef0123456789abcdef");
        var block = Vdf.Parse(text).Children!.Single();
        Assert.Equal(AppInfo.GsiConfigBlockName, block.Key);
        Assert.Equal("http://127.0.0.1:43570/", block["uri"]?.Value);
        Assert.Equal("0123456789abcdef0123456789abcdef", block.Get("auth", "token")?.Value);
        foreach (var node in new[] { "provider", "map", "player", "hero" })
            Assert.Equal("1", block.Get("data", node)?.Value);
    }

    [Fact]
    public void Gsi_config_install_and_check_roundtrip()
    {
        var dota = Path.Combine(Path.GetTempPath(), "dota2rpc-fake-dota-" + Guid.NewGuid().ToString("N"), "dota 2 beta");
        Directory.CreateDirectory(Path.Combine(dota, "game", "dota", "cfg"));
        try
        {
            Assert.Equal(GsiConfigState.Missing, GsiConfig.Check(dota, 43570, "0123456789abcdef0123456789abcdef").State);
            GsiConfig.Install(dota, 43570, "0123456789abcdef0123456789abcdef");
            Assert.Equal(GsiConfigState.Installed, GsiConfig.Check(dota, 43570, "0123456789abcdef0123456789abcdef").State);
            Assert.Equal(GsiConfigState.Outdated, GsiConfig.Check(dota, 43571, "0123456789abcdef0123456789abcdef").State);
            Assert.True(GsiConfig.Remove(dota));
            Assert.False(Directory.Exists(GsiConfig.ConfigDir(dota))); // пустая папка тоже убрана
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dota)!, recursive: true);
        }
    }

    /// <summary>
    /// Проверка на реальных файлах Steam этого компьютера (только чтение, в памяти):
    /// парсер читает libraryfolders.vdf и localconfig.vdf, а AddFlag меняет ровно одно место.
    /// </summary>
    [Fact]
    public void Real_steam_files_parse_and_edit_in_memory()
    {
        var steam = SteamLocator.FindSteamPath();
        if (steam is null) return; // Steam не установлен — проверять нечего

        Assert.NotEmpty(SteamLocator.LibraryFolders(steam));
        foreach (var info in LaunchOptions.ReadAll(steam))
        {
            var original = File.ReadAllText(info.ConfigPath, Encoding.UTF8);
            var updated = LaunchOptions.AddFlag(original);
            Assert.True(LaunchOptions.HasFlag(ReadOptions(updated)));
            if (info.HasFlag)
            {
                Assert.Same(original, updated);
                continue;
            }
            // Всё, кроме вставленного фрагмента, совпадает байт-в-байт.
            var prefix = 0;
            while (prefix < original.Length && original[prefix] == updated[prefix]) prefix++;
            var suffix = 0;
            while (suffix < original.Length - prefix && original[^(suffix + 1)] == updated[^(suffix + 1)]) suffix++;
            var inserted = updated.Length - original.Length;
            Assert.InRange(inserted, 1, 200);
            Assert.Equal(original.Length, prefix + suffix + (original.Length - prefix - suffix));
            Assert.Contains("-gamestateintegration", updated.Substring(prefix, updated.Length - prefix - suffix));
        }
    }
}
