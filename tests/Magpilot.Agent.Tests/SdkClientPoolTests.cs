using GitHub.Copilot;
using Magpilot.Agent.Runtime;
using Magpilot.Agent.Runtime.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkClientPoolTests
{
    [Fact]
    public async Task Same_client_scope_starts_one_runtime()
    {
        var factory = new FakeFactory();
        await using var pool = CreatePool(factory);
        var firstProfile = SessionRuntimeProfile.Resolve(
            useAgency: false,
            model: "model-a",
            reasoningEffort: "low");
        var secondProfile = SessionRuntimeProfile.Resolve(
            useAgency: false,
            model: "model-b",
            reasoningEffort: "high");

        var first = await pool.AcquireAsync(firstProfile, CancellationToken.None);
        var second = await pool.AcquireAsync(secondProfile, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(factory.Hosts);
        Assert.Equal(1, factory.Hosts[0].StartCount);
    }

    [Fact]
    public async Task Different_copilot_homes_start_isolated_runtimes()
    {
        var factory = new FakeFactory();
        await using var pool = CreatePool(factory);
        var first = SessionRuntimeProfile.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            copilotHome: Path.Combine(Path.GetTempPath(), "copilot-a"));
        var second = SessionRuntimeProfile.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            copilotHome: Path.Combine(Path.GetTempPath(), "copilot-b"));

        await pool.AcquireAsync(first, CancellationToken.None);
        await pool.AcquireAsync(second, CancellationToken.None);

        Assert.Equal(2, factory.Hosts.Count);
        Assert.All(factory.Hosts, static host => Assert.Equal(1, host.StartCount));
    }

    [Fact]
    public async Task Failed_start_is_removed_so_a_retry_can_create_a_new_client()
    {
        var factory = new FakeFactory(failFirstStart: true);
        await using var pool = CreatePool(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pool.AcquireAsync(SessionRuntimeProfile.Default, CancellationToken.None));
        var recovered = await pool.AcquireAsync(
            SessionRuntimeProfile.Default,
            CancellationToken.None);

        Assert.Same(factory.Hosts[1], recovered);
        Assert.Equal(2, factory.Hosts.Count);
        Assert.Equal(1, factory.Hosts[0].ForceStopCount);
        Assert.Equal(1, factory.Hosts[0].DisposeCount);
    }

    [Fact]
    public async Task Hosted_service_stop_gracefully_stops_and_disposes_clients()
    {
        var factory = new FakeFactory();
        var pool = CreatePool(factory);
        await pool.AcquireAsync(SessionRuntimeProfile.Default, CancellationToken.None);

        await pool.StopAsync(CancellationToken.None);

        var host = Assert.Single(factory.Hosts);
        Assert.Equal(1, host.StopCount);
        Assert.Equal(0, host.ForceStopCount);
        Assert.Equal(1, host.DisposeCount);
    }

    private static SdkClientPool CreatePool(FakeFactory factory) =>
        new(factory, NullLogger<SdkClientPool>.Instance);

    private sealed class FakeFactory(bool failFirstStart = false)
        : ISdkClientHostFactory
    {
        public List<FakeHost> Hosts { get; } = [];

        public ISdkClientHost Create(SdkClientKey key)
        {
            var host = new FakeHost(failFirstStart && Hosts.Count == 0);
            Hosts.Add(host);
            return host;
        }
    }

    private sealed class FakeHost(bool failStart) : ISdkClientHost
    {
        public CopilotClient Client => null!;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int ForceStopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            StartCount++;
            return failStart
                ? Task.FromException(new InvalidOperationException("start failed"))
                : Task.CompletedTask;
        }

        public Task<GetStatusResponse> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new GetStatusResponse
            {
                Version = "test-runtime",
                ProtocolVersion = 3,
            });

        public Task StopAsync()
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public Task ForceStopAsync()
        {
            ForceStopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
