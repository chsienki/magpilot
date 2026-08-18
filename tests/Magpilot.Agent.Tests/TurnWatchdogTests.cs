using Magpilot.Agent.Acp;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Locks the turn watchdog's stall decision: a wedged turn (no child activity
/// for the whole window) must be caught, while a live-but-slow turn -- one that
/// has emitted a chunk or tool call recently -- must not be, or the watchdog
/// would kill healthy work. The boundary and the "recent activity resets the
/// clock" behaviour are the load-bearing cases.
/// </summary>
public sealed class TurnWatchdogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(90);

    [Fact]
    public void No_activity_past_threshold_is_stalled()
    {
        // Turn started, never emitted anything, and the window has elapsed.
        Assert.True(AcpSessionManager.IsTurnStalled(T0, lastEventAt: null, now: T0.AddSeconds(90), Threshold));
    }

    [Fact]
    public void No_activity_before_threshold_is_not_stalled()
    {
        Assert.False(AcpSessionManager.IsTurnStalled(T0, lastEventAt: null, now: T0.AddSeconds(89), Threshold));
    }

    [Fact]
    public void Recent_activity_resets_the_clock()
    {
        // The turn is 120s old but streamed a chunk 40s ago -> healthy, not stalled.
        Assert.False(AcpSessionManager.IsTurnStalled(
            T0, lastEventAt: T0.AddSeconds(80), now: T0.AddSeconds(120), Threshold));
    }

    [Fact]
    public void Old_activity_then_silence_is_stalled()
    {
        // Emitted early, then went quiet for longer than the window.
        Assert.True(AcpSessionManager.IsTurnStalled(
            T0, lastEventAt: T0.AddSeconds(10), now: T0.AddSeconds(120), Threshold));
    }

    [Fact]
    public void Last_event_before_start_falls_back_to_start()
    {
        // A stale mapping from a prior turn must not make a fresh turn look active.
        Assert.True(AcpSessionManager.IsTurnStalled(
            T0, lastEventAt: T0.AddSeconds(-30), now: T0.AddSeconds(95), Threshold));
    }

    [Fact]
    public void Exactly_at_threshold_is_stalled()
    {
        Assert.True(AcpSessionManager.IsTurnStalled(T0, lastEventAt: null, now: T0 + Threshold, Threshold));
    }
}
