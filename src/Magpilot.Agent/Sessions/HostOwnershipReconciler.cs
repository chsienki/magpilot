using System.Diagnostics;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Reconstructs <see cref="HostOwnership"/> from live process ancestry so an
/// agent restart doesn't strand launcher-driven sessions as "kill to unlock".
///
/// <para>The launcher registers ownership once, at spawn, and never
/// re-asserts. An agent restart -- which happens on every re-pair and every
/// update -- wiped the in-memory map, so any interactive session a
/// <c>magpilot</c> launcher was still driving fell to <c>External</c> in the
/// SPA even though the launcher (and its copilot child) were alive. Persisting
/// the map (see <see cref="HostOwnership"/>) covers sessions the launcher had
/// recorded, but not ones started before persistence existed, or while the
/// agent was down.</para>
///
/// <para>This reconciler closes that gap from live reality: it scans the
/// session-state directory for live foreign locks and, for any whose lock
/// holder is a descendant of a <c>magpilot</c> launcher process (see
/// <see cref="ProcessAncestry"/>), records host ownership keyed on that
/// launcher PID. It runs once on startup -- retroactively reclaiming sessions
/// an older/un-persisted launcher was driving -- and on a periodic sweep for
/// robustness. The <c>HostOwnership</c> PID-liveness sweep still evicts the
/// entry when the launcher exits.</para>
/// </summary>
public sealed class HostOwnershipReconciler(
    SessionScanner scanner,
    HostOwnership hostOwnership,
    ILogger<HostOwnershipReconciler> logger,
    IConfiguration config) : BackgroundService
{
    private readonly TimeSpan _interval =
        TimeSpan.FromSeconds(config.GetValue("Agent:HostOwnershipReconcileSec", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Reconcile(); }
            catch (Exception ex) { logger.LogWarning(ex, "Host-ownership reconcile failed"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Reconcile()
    {
        var root = scanner.Root;
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var sid = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(sid)) continue;

            // Hygiene: drop advisory inuse.<pid>.lock files whose owner is gone,
            // so dead holders don't accumulate or later masquerade as a live
            // contention signal.
            SessionLocks.ReapDead(dir, f =>
                logger.LogInformation("Reaped stale session lock {File} (owner process gone)", f));

            // Already host-owned (persisted map, or a prior reconcile). TryGet
            // also prunes the entry if its holder has since died.
            if (hostOwnership.TryGet(sid, out _)) continue;

            var lockFile = SafeFirstLock(dir);
            if (lockFile is null) continue;
            if (!TryParseLockPid(lockFile, out var lockPid) || !IsAlive(lockPid)) continue;

            // A launcher-driven copilot is a descendant of `magpilot`; the
            // agent's own `copilot --acp` child is parented under
            // Magpilot.Agent, and a bare terminal copilot under a shell, so
            // neither false-matches here.
            if (ProcessAncestry.TryFindAncestorPidByName(lockPid, "magpilot", out var launcherPid))
            {
                hostOwnership.Set(sid, launcherPid);
                logger.LogInformation(
                    "Reconciled host ownership via process ancestry: sid={Sid} launcher={LauncherPid} copilot={LockPid}",
                    sid, launcherPid, lockPid);
            }
        }
    }

    private static string? SafeFirstLock(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "inuse.*.lock").FirstOrDefault(); }
        catch { return null; }
    }

    private static bool TryParseLockPid(string lockPath, out int pid)
    {
        pid = 0;
        var parts = Path.GetFileName(lockPath).Split('.');
        return parts.Length >= 3 && int.TryParse(parts[1], out pid);
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }
}
