using Magpilot.Agent.Runtime;
using Magpilot.Agent.Runtime.Sdk;
using Magpilot.Agent.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkClientPoolIntegrationTests
{
    [Fact]
    public async Task Bundled_runtime_starts_responds_and_stops()
    {
        var pool = new SdkClientPool(
            NullLoggerFactory.Instance,
            NullLogger<SdkClientPool>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            var host = await pool.AcquireAsync(
                SessionRuntimeProfile.Default,
                cts.Token);
            var response = await host.Client.PingAsync(
                "magpilot-sdk-pool-test",
                cts.Token);

            Assert.Equal("pong: magpilot-sdk-pool-test", response.Message);
        }
        finally
        {
            await pool.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sdk_session_recognizes_its_own_lock_and_detaches_cleanly()
    {
        var pool = new SdkClientPool(
            NullLoggerFactory.Instance,
            NullLogger<SdkClientPool>.Instance);
        var broker = new SdkPermissionBroker(
            new YoloRegistry(NullLogger<YoloRegistry>.Instance),
            NullLogger<SdkPermissionBroker>.Instance);
        var runtime = new SdkSessionRuntime(
            pool,
            broker,
            NullLogger<SdkSessionRuntime>.Instance);
        var profile = SessionRuntimeProfile.Resolve(
            model: null,
            reasoningEffort: null,
            backend: SessionRuntimeBackend.Sdk);
        string? sessionId = null;

        try
        {
            sessionId = await runtime.NewSessionAsync(
                Environment.CurrentDirectory,
                profile,
                CancellationToken.None);

            Assert.True(runtime.IsAttached(sessionId));
            Assert.False(runtime.HasForeignLiveHolder(sessionId));

            var detached = await runtime.CloseAsync(
                sessionId,
                sessionsRoot: null,
                CancellationToken.None);

            Assert.NotNull(detached);
            Assert.False(runtime.IsAttached(sessionId));
            Assert.False(runtime.HasForeignLiveHolder(sessionId));

            var host = await pool.AcquireAsync(
                profile,
                CancellationToken.None);
            await host.Client.DeleteSessionAsync(
                sessionId,
                CancellationToken.None);
            sessionId = null;
        }
        finally
        {
            if (sessionId is not null)
            {
                if (runtime.IsAttached(sessionId))
                {
                    await runtime.CloseAsync(
                        sessionId,
                        sessionsRoot: null,
                        CancellationToken.None);
                }
                var host = await pool.AcquireAsync(
                    profile,
                    CancellationToken.None);
                await host.Client.DeleteSessionAsync(
                    sessionId,
                    CancellationToken.None);
            }
            await pool.StopAsync(CancellationToken.None);
        }
    }
}
