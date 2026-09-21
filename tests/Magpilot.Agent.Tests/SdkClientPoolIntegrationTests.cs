using Magpilot.Agent.Runtime;
using Magpilot.Agent.Runtime.Sdk;
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
}
