using System.Diagnostics;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Inspection and hygiene for a session directory's advisory
/// <c>inuse.&lt;pid&gt;.lock</c> files. Copilot writes one per attached process
/// and they are advisory only, so a stale (dead-pid) lock -- or two live
/// holders -- can coexist. Two live holders is how a session ends up served
/// from one process's frozen in-memory snapshot while another advances it on
/// disk (copilot's session/load no-ops on an already-loaded session, so the
/// stale child never re-reads disk). These helpers make that state observable
/// and the dead locks reapable.
/// </summary>
public static class SessionLocks
{
    public readonly record struct Holder(string Path, int Pid, bool Alive);

    /// <summary>Parse the pid out of an <c>inuse.&lt;pid&gt;.lock</c> file name (or path).</summary>
    public static bool TryParsePid(string lockPath, out int pid)
    {
        pid = 0;
        var parts = System.IO.Path.GetFileName(lockPath).Split('.');
        return parts.Length >= 3
            && parts[0].Equals("inuse", StringComparison.Ordinal)
            && parts[^1].Equals("lock", StringComparison.Ordinal)
            && int.TryParse(parts[1], out pid);
    }

    /// <summary>
    /// Classify lock files by owner liveness. Pure: the caller supplies both
    /// the file list and the liveness predicate, so this is unit-testable
    /// without a real filesystem or process table.
    /// </summary>
    public static IReadOnlyList<Holder> Inspect(IEnumerable<string> lockPaths, Func<int, bool> isAlive)
    {
        var holders = new List<Holder>();
        foreach (var path in lockPaths)
            if (TryParsePid(path, out var pid))
                holders.Add(new Holder(path, pid, isAlive(pid)));
        return holders;
    }

    public static IReadOnlyList<Holder> Live(IReadOnlyList<Holder> holders) =>
        holders.Where(h => h.Alive).ToList();

    public static IReadOnlyList<Holder> Dead(IReadOnlyList<Holder> holders) =>
        holders.Where(h => !h.Alive).ToList();

    /// <summary>Default liveness check: is a process with this id running?</summary>
    public static bool ProcessAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    /// <summary>Lock files physically present in a session directory (empty if unreadable).</summary>
    public static IReadOnlyList<string> Files(string sessionDir)
    {
        try { return Directory.EnumerateFiles(sessionDir, "inuse.*.lock").ToList(); }
        catch { return []; }
    }

    /// <summary>Inspect a session directory's lock state against the real process table.</summary>
    public static IReadOnlyList<Holder> Inspect(string sessionDir) =>
        Inspect(Files(sessionDir), ProcessAlive);

    /// <summary>
    /// Delete lock files whose owning process is gone; returns the paths reaped.
    /// A live pid is never touched -- fail-safe against pid reuse: if the id was
    /// reused by an unrelated live process we keep the lock rather than risk
    /// deleting a valid one.
    /// </summary>
    public static IReadOnlyList<string> ReapDead(string sessionDir, Action<string>? onReaped = null)
    {
        var reaped = new List<string>();
        foreach (var holder in Dead(Inspect(sessionDir)))
        {
            try
            {
                File.Delete(holder.Path);
                reaped.Add(holder.Path);
                onReaped?.Invoke(holder.Path);
            }
            catch { /* another process may be mid-write; skip and retry next sweep */ }
        }
        return reaped;
    }
}
