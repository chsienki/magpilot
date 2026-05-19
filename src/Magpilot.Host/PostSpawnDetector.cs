namespace Magpilot.Host;

/// <summary>
/// Watches the on-disk copilot session-state directory for an
/// <c>inuse.&lt;pid&gt;.lock</c> file owned by a specific PID, returning the
/// session UUID once it appears. Used after spawning a copilot child in
/// the post-spawn-detection path: we don't know the session id up front
/// (e.g. user passed <c>--resume="some name"</c>, <c>--continue</c>, or
/// no flag at all), so we wait for copilot to take a lock and discover
/// the id from the lock file's parent directory.
///
/// <para>
/// Polls every 250ms. Defaults to a 30s timeout, which comfortably
/// covers copilot's startup time without making a never-took-a-lock
/// session (e.g. user fat-fingered <c>--resume=&lt;unknown&gt;</c> and
/// copilot exited immediately) hang the launcher.
/// </para>
///
/// <para>
/// Lookup is done by enumerating the well-known
/// <c>~/.copilot/session-state/&lt;sid&gt;/inuse.&lt;pid&gt;.lock</c> hierarchy
/// rather than watching files via FileSystemWatcher: the latter is
/// flaky on Windows for files-that-may-not-exist-yet, and the polling
/// budget is trivial.
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
    /// <c>inuse.&lt;pid&gt;.lock</c> under the session-state root, and return
    /// the containing session id. Returns null on timeout.
    /// </summary>
    public static async Task<string?> WaitForSessionAsync(int copilotPid, CancellationToken ct, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var root = SessionStateRoot;
        if (!Directory.Exists(root)) return null;

        var lockSuffix = $".{copilotPid}.lock";

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                foreach (var sessionDir in Directory.EnumerateDirectories(root))
                {
                    foreach (var file in Directory.EnumerateFiles(sessionDir, "inuse.*.lock"))
                    {
                        if (Path.GetFileName(file).EndsWith(lockSuffix, StringComparison.Ordinal))
                            return Path.GetFileName(sessionDir);
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
