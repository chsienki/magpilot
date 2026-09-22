using Magpilot.Agent.Runtime;
using Magpilot.Agent.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Covers the durability contract that keeps a launcher-driven session
/// Host-owned across an agent restart (the restart otherwise orphaned the
/// session into "kill to unlock"). Uses the current test process as a
/// conveniently-alive stand-in for a launcher holder.
/// </summary>
public sealed class HostOwnershipTests : IDisposable
{
    private readonly string _file =
        Path.Combine(Path.GetTempPath(), $"magpilot-hostown-{Guid.NewGuid():N}.json");

    private HostOwnership New()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:HostOwnershipFile"] = _file })
            .Build();
        return new HostOwnership(NullLogger<HostOwnership>.Instance, config);
    }

    [Fact]
    public async Task Live_owner_survives_a_restart()
    {
        var sid = Guid.NewGuid().ToString();
        New().Set(sid, Environment.ProcessId);   // persisted with a real, live PID

        var reloaded = New();
        await reloaded.StartAsync(default);       // simulates the agent restarting
        try
        {
            Assert.True(reloaded.TryGet(sid, out var entry));
            Assert.Equal(Environment.ProcessId, entry.HostPid);
        }
        finally
        {
            await reloaded.StopAsync(default);
            reloaded.Dispose();
        }
    }

    [Fact]
    public async Task Session_flavor_survives_a_restart()
    {
        var sid = Guid.NewGuid().ToString();
        var flavor = new HostSessionFlavor(
            Model: "example-model",
            ReasoningEffort: "low",
            DisabledMcpServers: ["example-server"],
            Agent: "magnus-phone",
            AvailableTools: ["magnus-phone", "server(tool_name)"],
            DisableBuiltinMcps: true,
            NoCustomInstructions: true,
            CopilotHome: Path.Combine(Path.GetTempPath(), "copilot-phone"),
            Backend: SessionRuntimeBackend.Sdk);
        New().Set(sid, Environment.ProcessId, flavor);

        var reloaded = New();
        await reloaded.StartAsync(default);
        try
        {
            Assert.True(reloaded.TryGet(sid, out var entry));
            Assert.NotNull(entry.Flavor);
            Assert.Equal(flavor.Model, entry.Flavor.Model);
            Assert.Equal(flavor.ReasoningEffort, entry.Flavor.ReasoningEffort);
            Assert.Equal(flavor.DisabledMcpServers, entry.Flavor.DisabledMcpServers);
            Assert.Equal(flavor.Agent, entry.Flavor.Agent);
            Assert.Equal(flavor.AvailableTools, entry.Flavor.AvailableTools);
            Assert.Equal(flavor.DisableBuiltinMcps, entry.Flavor.DisableBuiltinMcps);
            Assert.Equal(flavor.NoCustomInstructions, entry.Flavor.NoCustomInstructions);
            Assert.Equal(flavor.CopilotHome, entry.Flavor.CopilotHome);
            Assert.Equal(flavor.Backend, entry.Flavor.Backend);
        }
        finally
        {
            await reloaded.StopAsync(default);
            reloaded.Dispose();
        }
    }

    [Fact]
    public async Task Reused_pid_with_mismatched_start_time_is_dropped()
    {
        var sid = Guid.NewGuid().ToString();
        // Live PID, but a start time that doesn't match the process -- the
        // shape of a PID that a different process reused after a restart.
        WriteState(sid, Environment.ProcessId, hostStartTicks: DateTimeOffset.UtcNow.Ticks);

        var reloaded = New();
        await reloaded.StartAsync(default);
        try { Assert.False(reloaded.TryGet(sid, out _)); }
        finally { await reloaded.StopAsync(default); reloaded.Dispose(); }
    }

    [Fact]
    public async Task Dead_pid_is_dropped_on_load()
    {
        var sid = Guid.NewGuid().ToString();
        WriteState(sid, hostPid: 2147483646, hostStartTicks: 0);  // implausible PID => not alive

        var reloaded = New();
        await reloaded.StartAsync(default);
        try { Assert.False(reloaded.TryGet(sid, out _)); }
        finally { await reloaded.StopAsync(default); reloaded.Dispose(); }
    }

    [Fact]
    public async Task Dead_owner_flavor_remains_available_for_release_after_restart()
    {
        var sid = Guid.NewGuid().ToString();
        const int deadPid = 2147483646;
        var flavor = new HostSessionFlavor(
            Model: "example-model",
            ReasoningEffort: "none",
            DisabledMcpServers: ["example-server"]);
        New().Set(sid, deadPid, flavor);

        var reloaded = New();
        await reloaded.StartAsync(default);
        try
        {
            Assert.False(reloaded.TryGet(sid, out _));
            Assert.True(reloaded.TryGetRecorded(sid, out var entry));
            Assert.Equal(deadPid, entry.HostPid);
            Assert.NotNull(entry.Flavor);
            Assert.Equal(flavor.Model, entry.Flavor.Model);
            Assert.Equal(flavor.ReasoningEffort, entry.Flavor.ReasoningEffort);
            Assert.Equal(flavor.DisabledMcpServers, entry.Flavor.DisabledMcpServers);
        }
        finally
        {
            await reloaded.StopAsync(default);
            reloaded.Dispose();
        }
    }

    private void WriteState(string sid, int hostPid, long hostStartTicks) =>
        File.WriteAllText(_file,
            $"[{{\"SessionId\":\"{sid}\",\"LeaseId\":\"{Guid.NewGuid()}\",\"HostPid\":{hostPid}," +
            $"\"AcquiredAt\":\"{DateTimeOffset.UtcNow:o}\",\"HostStartTicks\":{hostStartTicks}}}]");

    public void Dispose()
    {
        try { File.Delete(_file); } catch { /* best effort */ }
    }
}
