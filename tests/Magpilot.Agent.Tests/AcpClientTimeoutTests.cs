using Magpilot.Agent.Acp;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class AcpClientTimeoutTests
{
    [Fact]
    public async Task Caller_cancellation_surfaces_operation_canceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AcpClient.WaitWithTimeoutCoreAsync(
                id: 42,
                TaskCompletionSourceTask(),
                cts.Token,
                TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Timeout_still_surfaces_timeout_exception()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            AcpClient.WaitWithTimeoutCoreAsync(
                id: 42,
                TaskCompletionSourceTask(),
                CancellationToken.None,
                TimeSpan.FromMilliseconds(20)));

        Assert.Contains("id=42", ex.Message);
    }

    [Fact]
    public async Task Completed_call_returns_its_result()
    {
        var result = await AcpClient.WaitWithTimeoutCoreAsync(
            id: 42,
            Task.FromResult("done"),
            CancellationToken.None,
            TimeSpan.FromSeconds(10));

        Assert.Equal("done", result);
    }

    private static Task<string> TaskCompletionSourceTask() =>
        new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
}
