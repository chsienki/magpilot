using Magpilot.Hub.Agents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Hub.Tests;

public sealed class AgentRegistryTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("magpilot-agent-registry-test");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Delete(recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remove_deletes_agent_and_credentials_across_restart(bool revoke)
    {
        var registry = CreateRegistry();
        registry.Upsert("alpha", "http://alpha.test", "agent-token", online: true);
        if (revoke)
            registry.Revoke("alpha");

        Assert.True(registry.Remove("alpha"));

        Assert.Null(registry.Get("alpha"));
        Assert.Null(registry.GetToken("alpha"));
        var restarted = CreateRegistry();
        Assert.Empty(restarted.List());
        Assert.Null(restarted.GetToken("alpha"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovery_can_rediscover_removed_agent_without_restoring_pairing(bool restart)
    {
        var registry = CreateRegistry();
        registry.Upsert("alpha", "http://alpha.test", "agent-token", online: true);
        registry.Remove("alpha");
        if (restart)
            registry = CreateRegistry();

        registry.Upsert("alpha", "http://alpha.test", token: null, online: true);

        var discovered = Assert.Single(registry.List());
        Assert.True(discovered.Online);
        Assert.False(discovered.IsPaired);
        Assert.Null(discovered.EnrolledAt);
        Assert.Null(registry.GetToken("alpha"));
        Assert.False(Assert.Single(CreateRegistry().List()).IsPaired);
    }

    [Fact]
    public void Fresh_credentials_restore_pairing_after_removal_and_discovery()
    {
        var registry = CreateRegistry();
        registry.Upsert("alpha", "http://alpha.test", "agent-token", online: true);
        registry.Remove("alpha");
        registry.Upsert("alpha", "http://alpha.test", token: null, online: true);

        registry.Upsert("alpha", "", "new-token", online: false);
        registry.Reload("alpha");
        var restarted = CreateRegistry();
        restarted.Upsert("alpha", "http://alpha.test", token: null, online: true);

        Assert.True(Assert.Single(restarted.List()).IsPaired);
        Assert.True(restarted.Get("alpha")!.IsPaired);
        Assert.Equal("new-token", restarted.GetToken("alpha"));
    }

    [Fact]
    public void Discovery_preserves_older_pairing_and_cannot_restore_revoked_pairing()
    {
        var registry = CreateRegistry();

        registry.Upsert("alpha", "http://alpha.test", "agent-token", online: true);

        registry.Upsert("alpha", "http://alpha.test", token: null, online: true);

        var paired = Assert.Single(CreateRegistry().List());
        Assert.Null(paired.EnrolledAt);
        Assert.True(paired.IsPaired);
        Assert.True(registry.Get("alpha")!.IsPaired);
        registry.Revoke("alpha");
        registry.Upsert("alpha", "http://alpha.test", token: null, online: true);
        Assert.False(Assert.Single(registry.List()).IsPaired);
        Assert.False(registry.Get("alpha")!.IsPaired);
        Assert.False(Assert.Single(CreateRegistry().List()).IsPaired);
    }

    private AgentRegistry CreateRegistry()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:DataDir"] = _directory.FullName,
            })
            .Build();
        return new AgentRegistry(configuration, NullLogger<AgentRegistry>.Instance);
    }
}
