using Magpilot.Agent.Runtime;

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
public sealed class HostOwnershipReconciler : BackgroundService
{
    private readonly SessionScanner _scanner;
    private readonly HostOwnership _hostOwnership;
    private readonly ILogger<HostOwnershipReconciler> _logger;
    private readonly Func<string, bool> _isRuntimeResident;
    private readonly Func<int, (bool Found, int LauncherPid)> _findLauncher;
    private readonly Func<string, IReadOnlyList<int>> _liveLockPids;
    private readonly TimeSpan _interval;

    public HostOwnershipReconciler(
        SessionScanner scanner,
        HostOwnership hostOwnership,
        ILogger<HostOwnershipReconciler> logger,
        IConfiguration config,
        IAgentSessionRuntime runtime)
        : this(
            scanner,
            hostOwnership,
            logger,
            config,
            runtime.IsResident,
            FindLauncher,
            LiveLockPids)
    {
    }

    internal HostOwnershipReconciler(
        SessionScanner scanner,
        HostOwnership hostOwnership,
        ILogger<HostOwnershipReconciler> logger,
        IConfiguration config,
        Func<string, bool> isRuntimeResident,
        Func<int, (bool Found, int LauncherPid)> findLauncher,
        Func<string, IReadOnlyList<int>>? liveLockPids = null)
    {
        _scanner = scanner;
        _hostOwnership = hostOwnership;
        _logger = logger;
        _isRuntimeResident = isRuntimeResident;
        _findLauncher = findLauncher;
        _liveLockPids = liveLockPids ?? LiveLockPids;
        _interval = TimeSpan.FromSeconds(
            config.GetValue("Agent:HostOwnershipReconcileSec", 60));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Reconcile(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Host-ownership reconcile failed"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void Reconcile()
    {
        var root = _scanner.Root;
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var sid = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(sid)) continue;

            // Hygiene: drop advisory inuse.<pid>.lock files whose owner is gone,
            // so dead holders don't accumulate or later masquerade as a live
            // contention signal.
            SessionLocks.ReapDead(dir, f =>
                _logger.LogInformation("Reaped stale session lock {File} (owner process gone)", f));

            // Already host-owned (persisted map, or a prior reconcile). TryGet
            // also prunes the entry if its holder has since died.
            if (_hostOwnership.TryGet(sid, out _)) continue;

            // A runtime-owned lock can still have a magpilot ancestor in
            // development, where Magpilot.Agent is launched from a terminal
            // session. Runtime residency is authoritative; never reinterpret
            // the active agent's own session as terminal-owned.
            if (_isRuntimeResident(sid)) continue;

            foreach (var lockPid in _liveLockPids(dir))
            {
                // A launcher-driven Copilot is a descendant of `magpilot`; the
                // agent's own SDK runtime is parented under Magpilot.Agent, and a
                // bare terminal Copilot under a shell, so neither false-matches.
                var (found, launcherPid) = _findLauncher(lockPid);
                if (!found)
                    continue;

                _hostOwnership.Set(sid, launcherPid);
                _logger.LogInformation(
                    "Reconciled host ownership via process ancestry: sid={Sid} launcher={LauncherPid} copilot={LockPid}",
                    sid, launcherPid, lockPid);
                break;
            }
        }
    }

    private static (bool Found, int LauncherPid) FindLauncher(int lockPid) =>
        ProcessAncestry.TryFindAncestorPidByName(
            lockPid,
            "magpilot",
            out var launcherPid)
            ? (true, launcherPid)
            : (false, 0);

    private static IReadOnlyList<int> LiveLockPids(string directory) =>
        SessionLocks.ReadSnapshot(directory).Live
            .Select(holder => holder.Pid)
            .Distinct()
            .ToArray();
}
