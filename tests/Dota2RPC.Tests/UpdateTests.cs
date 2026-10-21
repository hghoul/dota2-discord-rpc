using Dota2RPC.Core;
using Xunit;

namespace Dota2RPC.Tests;

public class UpdateTests
{
    const string Sha = "21CD304E9EA940ACB4472FCD709DD5DDD844D7B4E5D6FFC3AF5D59601E7EBC3C";

    static string Release(string tag, string assetUrl, string body = "", bool prerelease = false) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/hghoul/dota2-discord-rpc/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "{{body}}",
          "assets": [
            { "name": "Setup.exe", "browser_download_url": "{{assetUrl}}" },
            { "name": "other.zip", "browser_download_url": "https://github.com/hghoul/dota2-discord-rpc/releases/download/{{tag}}/other.zip" }
          ]
        }
        """;

    const string Url = "https://github.com/hghoul/dota2-discord-rpc/releases/download/v1.2.0/Setup.exe";

    [Fact]
    public void Parses_version_setup_url_and_checksum()
    {
        var info = UpdateChecker.ParseRelease(Release("v1.2.0", Url, $"Собрано GitHub Actions. SHA256 Setup.exe: `{Sha.ToLowerInvariant()}`"))!;
        Assert.Equal(new Version(1, 2, 0), info.Version);
        Assert.Equal(Url, info.SetupUrl);
        Assert.Equal(Sha, info.Sha256);
        Assert.True(info.Version > new Version(1, 1, 0));
        Assert.False(new Version(1, 0, 0) > new Version(1, 0, 0));
    }

    [Fact]
    public void Rejects_foreign_download_and_prerelease()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release("v1.2.0", "https://evil.example/Setup.exe")));
        Assert.Null(UpdateChecker.ParseRelease(Release("v1.2.0", "https://github.com/someone/fork/releases/download/v1.2.0/Setup.exe")));
        Assert.Null(UpdateChecker.ParseRelease(Release("v1.2.0", Url, prerelease: true)));
        Assert.Null(UpdateChecker.ParseRelease(Release("latest", Url)));
    }

    [Fact]
    public void Missing_checksum_is_allowed_but_reported_as_null()
    {
        var info = UpdateChecker.ParseRelease(Release("v1.2.0", Url, "без суммы"))!;
        Assert.Null(info.Sha256);
    }

    /// <summary>Живая проверка с GitHub (только если задана DOTA2RPC_LIVE_GITHUB=1, чтобы CI не упирался в лимиты API).</summary>
    [Fact]
    public async Task Live_latest_release_is_parsed()
    {
        if (Environment.GetEnvironmentVariable("DOTA2RPC_LIVE_GITHUB") != "1") return;
        var json = await Http.Client.GetStringAsync($"https://api.github.com/repos/{UpdateChecker.Repository}/releases/latest");
        var info = UpdateChecker.ParseRelease(json);
        Assert.NotNull(info);
        Assert.NotNull(info!.Sha256);
        Assert.StartsWith($"https://github.com/{UpdateChecker.Repository}/releases/download/", info.SetupUrl);

        // Настоящая загрузка с проверкой SHA256 из описания релиза.
        var path = await UpdateChecker.DownloadAsync(info, null);
        try
        {
            Assert.True(new FileInfo(path).Length > 10_000_000);
        }
        finally
        {
            File.Delete(path);
        }

        // Подменённая сумма — загрузка должна быть отклонена, а файл удалён.
        var tampered = info with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateChecker.DownloadAsync(tampered, null));
    }
}
