using Dota2RPC.Core;
using Xunit;

namespace Dota2RPC.Tests;

[Collection("embedded-client-id")]
public class ClientIdTests
{
    const string Embedded = "111111111111111111";
    const string Own = "222222222222222222";

    [Fact]
    public void Embedded_id_is_used_when_user_did_not_set_own()
    {
        var saved = AppInfo.EmbeddedClientId;
        AppInfo.EmbeddedClientId = Embedded;
        try
        {
            var s = TestEnv.Settings();
            Assert.Equal(Embedded, s.EffectiveClientId);
            Assert.True(s.UsesEmbeddedClientId);

            s.DiscordClientId = Own;
            Assert.Equal(Own, s.EffectiveClientId);
            Assert.False(s.UsesEmbeddedClientId);

            s.DiscordClientId = "not-an-id";
            Assert.Equal(Embedded, s.EffectiveClientId);
        }
        finally
        {
            AppInfo.EmbeddedClientId = saved;
        }
    }

    [Fact]
    public void Without_embedded_id_only_own_id_counts()
    {
        var saved = AppInfo.EmbeddedClientId;
        AppInfo.EmbeddedClientId = "";
        try
        {
            var s = TestEnv.Settings();
            Assert.Equal("", s.EffectiveClientId);
            Assert.False(s.UsesEmbeddedClientId);
            s.DiscordClientId = Own;
            Assert.Equal(Own, s.EffectiveClientId);
        }
        finally
        {
            AppInfo.EmbeddedClientId = saved;
        }
    }

    [Fact]
    public void Animated_icon_used_only_for_heroes_in_manifest()
    {
        var savedBase = HeroAnimations.BaseUrl;
        HeroAnimations.BaseUrl = "https://cdn.jsdelivr.net/gh/owner/dota2rpc-assets@v1/";
        try
        {
            HeroAnimations.SetAvailable(HeroAnimations.ParseManifest("""{"version":1,"heroes":["pudge","../evil"]}"""));
            var catalog = new HeroCatalog(allowNetwork: false);
            Assert.Equal("https://cdn.jsdelivr.net/gh/owner/dota2rpc-assets@v1/heroes/pudge.webp", catalog.ImageUrl("npc_dota_hero_pudge", animated: true));
            Assert.Equal(HeroCatalog.HeroImageUrl("pudge"), catalog.ImageUrl("npc_dota_hero_pudge", animated: false));
            Assert.Equal(HeroCatalog.HeroImageUrl("axe"), catalog.ImageUrl("npc_dota_hero_axe", animated: true)); // нет в манифесте
            Assert.Null(HeroAnimations.TryGetUrl("../evil"));                                                     // имя отброшено
        }
        finally
        {
            HeroAnimations.BaseUrl = savedBase;
            HeroAnimations.SetAvailable([]);
        }
    }

    [Fact]
    public void Effective_id_is_not_written_to_settings_file()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(TestEnv.Settings());
        Assert.DoesNotContain("EffectiveClientId", json);
        Assert.DoesNotContain("UsesEmbeddedClientId", json);
    }
}
