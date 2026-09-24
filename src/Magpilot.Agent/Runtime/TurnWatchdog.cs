namespace Magpilot.Agent.Runtime;

/// <summary>
/// Periodically sweeps for in-flight SDK turns that have stopped producing
/// events and asks the runtime to abort them. Without this, a hung model
/// request can pin the session in flight and block later turns.
///
/// A live turn keeps emitting tool-call / message-chunk updates, so the sweep
/// only fires when a session has produced nothing for the stall window. The
/// threshold is deliberately generous (a slow first token on a large context is
/// normal); tune it with MAGPILOT_TURN_STALL_SECONDS.
/// </summary>
public sealed class TurnWatchdog(
    IAgentSessionRuntime runtime,
    ILogger<TurnWatchdog> log) : BackgroundService
{
    private const int DefaultStallSeconds = 90;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stallSeconds = DefaultStallSeconds;
        if (int.TryParse(Environment.GetEnvironmentVariable("MAGPILOT_TURN_STALL_SECONDS"), out var env) && env > 0)
            stallSeconds = env;

        var threshold = TimeSpan.FromSeconds(stallSeconds);
        // Sweep several times per window so a wedge is caught within ~1.3x the
        // threshold, without polling so often it shows up as noise.
        var interval = TimeSpan.FromSeconds(Math.Max(15, stallSeconds / 3));
        log.LogInformation(
            "Turn watchdog armed: stall threshold {Stall}s, sweeping every {Interval}s",
            stallSeconds, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // Bound the sweep so a hung abort cannot wedge the watchdog
                    // itself; the next tick retries.
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(90));
                    var recovered = await runtime.SweepStalledTurnsAsync(
                        threshold,
                        cts.Token);
                    if (recovered > 0)
                        log.LogWarning("Turn watchdog recovered {Count} stalled session(s)", recovered);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning("Turn watchdog: a sweep timed out");
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Turn watchdog sweep threw");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
