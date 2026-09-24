using Magpilot.Agent.Runtime.Sdk;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Locks the SDK watchdog's stall decision: a wedged turn (no runtime activity
/// for the whole window) must be caught, while a live-but-slow turn -- one that
/// has emitted a chunk or tool call recently -- must not be, or the watchdog
/// would abort healthy work.
/// </summary>
public sealed class TurnWatchdogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(90);

    [Fact]
    public void No_activity_past_threshold_is_stalled()
    {
        // Turn started, never emitted anything, and the window has elapsed.
        Assert.True(SdkSessionRuntime.IsTurnStalled(
            T0,
            T0.AddSeconds(90),
            Threshold));
    }

    [Fact]
    public void No_activity_before_threshold_is_not_stalled()
    {
        Assert.False(SdkSessionRuntime.IsTurnStalled(
            T0,
            T0.AddSeconds(89),
            Threshold));
    }

    [Fact]
    public void Recent_activity_resets_the_clock()
    {
        // The turn is 120s old but streamed a chunk 40s ago -> healthy, not stalled.
        Assert.False(SdkSessionRuntime.IsTurnStalled(
            T0.AddSeconds(80),
            T0.AddSeconds(120),
            Threshold));
    }

    [Fact]
    public void Old_activity_then_silence_is_stalled()
    {
        // Emitted early, then went quiet for longer than the window.
        Assert.True(SdkSessionRuntime.IsTurnStalled(
            T0.AddSeconds(10),
            T0.AddSeconds(120),
            Threshold));
    }

    [Fact]
    public void Exactly_at_threshold_is_stalled()
    {
        Assert.True(SdkSessionRuntime.IsTurnStalled(
            T0,
            T0 + Threshold,
            Threshold));
    }

    [Fact]
    public void Open_tool_call_is_never_stalled_even_far_past_threshold()
    {
        // A turn silent for ten minutes but waiting on a tool it invoked -- a long
        // shell command, a slow MCP call -- is busy, not wedged, and must survive.
        Assert.False(SdkSessionRuntime.IsTurnStalled(
            T0.AddSeconds(1),
            T0.AddSeconds(600),
            Threshold,
            hasOpenToolCall: true));
    }

    [Fact]
    public void No_open_tool_call_past_threshold_is_stalled()
    {
        // The same elapsed silence with no tool pending is a hung model -> recover.
        Assert.True(SdkSessionRuntime.IsTurnStalled(
            T0,
            T0.AddSeconds(120),
            Threshold,
            hasOpenToolCall: false));
    }
}
