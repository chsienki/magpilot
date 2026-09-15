using Magpilot.Agent.Update;
using Magpilot.Shared;
using Xunit;

namespace Magpilot.Agent.Tests;

public class LatestVersionCacheTests
{
    [Fact]
    public void Get_recomputes_update_for_stale_launcher()
    {
        var cache = new LatestVersionCache();
        cache.Set(new LatestVersionInfo(
            LatestVersion: "0.1.33",
            MinProtocol: 1,
            MaxProtocol: 1,
            UpdateAvailable: false));

        var result = cache.Get("0.1.28");

        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.1.33", result.LatestVersion);
    }

    [Fact]
    public void Get_reports_current_launcher_as_up_to_date()
    {
        var cache = new LatestVersionCache();
        cache.Set(new LatestVersionInfo(
            LatestVersion: "0.1.33",
            MinProtocol: 1,
            MaxProtocol: 1,
            UpdateAvailable: true));

        var result = cache.Get("0.1.33");

        Assert.False(result.UpdateAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void Get_without_valid_launcher_version_does_not_claim_an_update(string? from)
    {
        var cache = new LatestVersionCache();
        cache.Set(new LatestVersionInfo(
            LatestVersion: "0.1.33",
            MinProtocol: 1,
            MaxProtocol: 1,
            UpdateAvailable: true));

        Assert.False(cache.Get(from).UpdateAvailable);
    }
}
