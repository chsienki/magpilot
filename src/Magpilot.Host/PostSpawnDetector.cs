namespace Magpilot.Host;

/// <summary>
/// Watches the on-disk copilot session-state directory for the session
/// copilot ended up taking, returning its UUID. Used after spawning a
/// copilot child in the post-spawn-detection path: we don't know the
/// session id up front (e.g. user passed <c>--resume="some name"</c>,
/// <c>--resume=&lt;id-prefix&gt;</c>, <c>--continue</c>, or no flag at all),
/// so we wait for copilot to take a session and discover the id from
/// the on-disk artifacts.
///
/// <para>
/// Detection runs two passes per poll tick. The first matches the
/// spawned child's PID against any <c>inuse.&lt;pid&gt;.lock</c>; this is
/// what fresh sessions take. The second compares the mtime of
/// <c>events.jsonl</c> and <c>workspace.yaml</c> against the snapshot
/// captured at spawn time and returns a session whose either file has
/// been touched since; this is what catches resumes of existing
/// sessions, where copilot adds a fresh <c>inuse.&lt;pid&gt;.lock</c> in
/// most cases but for some sessions (notably empty ones with no
/// <c>events.jsonl</c> yet) leaves only a workspace.yaml touch as the
/// detectable signal.
/// </para>
///
/// <para>
/// Polls every 250ms. Defaults to a 30s timeout, which comfortably
/// covers copilot's startup time without making a never-took-a-session
/// child (e.g. user fat-fingered <c>--resume=&lt;unknown&gt;</c> and
/// copilot exited immediately) hang the launcher.
/// </para>
///
/// <para>
/// Lookup is done by enumerating the well-known
/// <c>~/.copilot/session-state/&lt;sid&gt;/...</c> hierarchy rather than
/// watching files via FileSystemWatcher: the latter is flaky on
/// Windows for files-that-may-not-exist-yet, and the polling budget
/// is trivial.
/// </para>
/// </summary>
internal static class PostSpawnDetector
{
    private static readonly TimeSpan DefaultTimeout  = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval    = TimeSpan.FromMilliseconds(250);

    private static string SessionStateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "session-state");

    /// <summary>
    /// Wait until <paramref name="copilotPid"/> appears as the PID in any
    /// <c>inuse.&lt;pid&gt;.lock</c> under the session-state root, OR until a
    /// session whose existing lock holder is dead has its
    /// <c>events.jsonl</c> or <c>workspace.yaml</c> mtime advance past
    /// spawn time (the fallback for resumes of existing sessions that
    /// don't refresh the lock). Returns the containing session id on
    /// either signal, or null on timeout.
    /// </summary>
    public static async Task<string?> WaitForSessionAsync(int copilotPid, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var root = SessionStateRoot;
        if (!Directory.Exists(root)) return null;

        // Snapshot mtimes for the fallback pass. Captured BEFORE the poll
        // loop so any touch that lands within the first 250ms tick is
        // detectable. Only tracks sessions whose lock holder is dead --
        // that's the resume-of-stranded-session shape we're looking for.
        // Live-lock sessions are excluded so unrelated activity on the
        // box (the user's other copilot process, a magpilot agent driving
        // a session via ACP) can never false-positive this pass.
        var beforeEvents    = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var beforeWorkspace = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var sessionDir in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(sessionDir);
            if (name is null) continue;
            if (!HasOnlyDeadLocks(sessionDir)) continue;
            var events = Path.Combine(sessionDir, "events.jsonl");
            if (File.Exists(events)) beforeEvents[name] = File.GetLastWriteTimeUtc(events);
            var ws = Path.Combine(sessionDir, "workspace.yaml");
            if (File.Exists(ws)) beforeWorkspace[name] = File.GetLastWriteTimeUtc(ws);
        }

        var lockSuffix = $".{copilotPid}.lock";

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                // Pass 1: exact PID match on inuse.<pid>.lock -- the canonical
                // signal for fresh sessions and most resumes.
                foreach (var sessionDir in Directory.EnumerateDirectories(root))
                {
                    foreach (var file in Directory.EnumerateFiles(sessionDir, "inuse.*.lock"))
                    {
                        if (Path.GetFileName(file).EndsWith(lockSuffix, StringComparison.Ordinal))
                            return Path.GetFileName(sessionDir);
                    }
                }

                // Pass 2: stranded-resume fallback. A pre-existing dead-lock
                // session whose events.jsonl or workspace.yaml just advanced
                // is the resume target. Re-validates the dead-lock condition
                // each tick so a session that just got a fresh live lock
                // (handled by Pass 1) doesn't also fire here.
                foreach (var sessionDir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(sessionDir);
                    if (name is null) continue;
                    if (!beforeEvents.ContainsKey(name) && !beforeWorkspace.ContainsKey(name)) continue;
                    if (!HasOnlyDeadLocks(sessionDir)) continue;

                    if (beforeEvents.TryGetValue(name, out var beforeEv))
                    {
                        var events = Path.Combine(sessionDir, "events.jsonl");
                        if (File.Exists(events) && File.GetLastWriteTimeUtc(events) > beforeEv)
                            return name;
                    }
                    if (beforeWorkspace.TryGetValue(name, out var beforeWs))
                    {
                        var ws = Path.Combine(sessionDir, "workspace.yaml");
                        if (File.Exists(ws) && File.GetLastWriteTimeUtc(ws) > beforeWs)
                            return name;
                    }
                }
            }
            catch (Exception)
            {
                // Directory contents can churn while we enumerate. Just try again.
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// True iff <paramref name="sessionDir"/> has at least one
    /// <c>inuse.&lt;pid&gt;.lock</c> file AND every such file names a PID
    /// that is no longer alive. A directory with no locks at all
    /// returns false -- "dormant" is a different state from "stranded".
    /// </summary>
    private static bool HasOnlyDeadLocks(string sessionDir)
    {
        var any = false;
        foreach (var file in SafeEnumerateFiles(sessionDir, "inuse.*.lock"))
        {
            any = true;
            var parts = Path.GetFileName(file).Split('.');
            if (parts.Length < 3 || !int.TryParse(parts[1], out var pid)) return false;
            try
            {
                using var _ = System.Diagnostics.Process.GetProcessById(pid);
                return false;
            }
            catch { /* dead, keep scanning */ }
        }
        return any;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Find the PID currently holding the named session, if any. Used by
    /// <c>--magpilot-claim</c> to discover the copilot PID it should
    /// register as the host owner. Returns null if no lock file exists.
    /// </summary>
    public static int? FindHolderPid(string sessionId)
    {
        var dir = Path.Combine(SessionStateRoot, sessionId);
        if (!Directory.Exists(dir)) return null;
        foreach (var file in Directory.EnumerateFiles(dir, "inuse.*.lock"))
        {
            var name = Path.GetFileName(file);
            // inuse.<pid>.lock -- middle segment is the PID.
            var parts = name.Split('.');
            if (parts.Length >= 3 && int.TryParse(parts[1], out var pid))
                return pid;
        }
        return null;
    }
}
