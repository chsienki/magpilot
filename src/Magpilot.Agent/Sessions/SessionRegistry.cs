using System.Collections.Concurrent;
using System.Diagnostics;
using Magpilot.Agent.Acp;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Bridges on-disk Copilot CLI sessions with the live ACP process.
/// Maintains the set of "owned" sessionIds (those currently loaded into
/// our ACP child) and orchestrates adoption of locked sessions.
/// </summary>
public sealed class SessionRegistry
{
    private readonly AcpSessionManager _acp;
    private readonly SessionScanner _scanner;
    private readonly ILogger<SessionRegistry> _logger;
    private readonly ConcurrentDictionary<string, byte> _owned = new();

    public SessionRegistry(AcpSessionManager acp, SessionScanner scanner, ILogger<SessionRegistry> logger)
    {
        _acp = acp;
        _scanner = scanner;
        _logger = logger;
    }

    public IReadOnlySet<string> Owned => _owned.Keys.ToHashSet();

    public IReadOnlyList<SessionInfo> List() => _scanner.Enumerate(Owned).ToList();

    public SessionInfo? Get(string id) => _scanner.Get(id, Owned);

    public async Task<SessionInfo> CreateAsync(string? cwd, bool useAgency, CancellationToken ct, string? name = null)
    {
        cwd ??= Environment.CurrentDirectory;
        var flavor = useAgency ? AcpFlavor.Agency : AcpFlavor.Default;
        var sid = await _acp.NewSessionAsync(cwd, flavor, ct);
        _owned.TryAdd(sid, 0);

        // Stamp a friendly name into workspace.yaml if the caller supplied
        // one. Copilot CLI auto-derives a summary from the first user
        // message, which is awful for sessions whose first message is a
        // long prompt template (e.g. cron heartbeats). A user-set name
        // overrides that.
        if (!string.IsNullOrWhiteSpace(name))
            TryWriteWorkspaceField(sid, "name", name, "summary", name);

        return _scanner.Get(sid, Owned)
            ?? new SessionInfo(sid, SessionState.Owned, cwd, null, null, name, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static void TryWriteWorkspaceField(string sessionId, params string[] keyValuePairs)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "session-state", sessionId);
            var path = Path.Combine(dir, "workspace.yaml");
            // workspace.yaml is written by the Copilot CLI shortly after
            // session/new returns. Wait briefly for it to appear.
            for (var i = 0; i < 30 && !File.Exists(path); i++)
                Thread.Sleep(100);
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path).ToList();
            // Build a fresh literal-quoted value for each key. Drop any
            // existing entry first (including multi-line literal blocks).
            for (var i = 0; i < keyValuePairs.Length; i += 2)
            {
                var k = keyValuePairs[i];
                var v = keyValuePairs[i + 1];
                // Remove existing key (and any continuation lines if it was
                // a literal block scalar).
                for (var j = 0; j < lines.Count; j++)
                {
                    if (lines[j].StartsWith(k + ":", StringComparison.Ordinal))
                    {
                        lines.RemoveAt(j);
                        // Strip continuation lines (indented) from a |- block.
                        while (j < lines.Count && (lines[j].StartsWith("  ") || string.IsNullOrWhiteSpace(lines[j])))
                            lines.RemoveAt(j);
                        break;
                    }
                }
                // Always quote so the line-based parser reads cleanly.
                lines.Add($"{k}: \"{v.Replace("\"", "\\\"")}\"");
            }
            File.WriteAllLines(path, lines);
        }
        catch { /* best-effort -- session is still usable without the name */ }
    }

    /// <summary>
    /// Adopt: if the session is held by another process, kill it (force=true required),
    /// then session/load it into our ACP child.
    /// Adopted sessions always use the default flavor; the original flavor is
    /// not persisted on disk (yet).
    /// </summary>
    public async Task<SessionInfo> AdoptAsync(string sessionId, bool force, CancellationToken ct)
    {
        var info = _scanner.Get(sessionId, Owned)
            ?? throw new FileNotFoundException($"Session {sessionId} not on disk");

        if (info.State == SessionState.Owned)
            return info;

        if (info.State == SessionState.Locked)
        {
            if (!force) throw new InvalidOperationException("Session is held by another process; pass force=true to take over.");
            if (info.OwnerPid is int pid)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    _logger.LogWarning("Killing PID {Pid} to adopt session {Sid}", pid, sessionId);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not kill PID {Pid}", pid); }
            }
            // Wait briefly for the lock file to vanish
            for (var i = 0; i < 20; i++)
            {
                var refreshed = _scanner.Get(sessionId, Owned);
                if (refreshed?.State == SessionState.Dormant) break;
                await Task.Delay(100, ct);
            }
        }

        var cwd = info.Cwd ?? Environment.CurrentDirectory;
        await _acp.LoadSessionAsync(sessionId, cwd, AcpFlavor.Default, ct);
        _owned.TryAdd(sessionId, 0);
        return _scanner.Get(sessionId, Owned) ?? info with { State = SessionState.Owned };
    }

    public async Task DetachAsync(string sessionId, CancellationToken ct)
    {
        if (_owned.TryRemove(sessionId, out _))
            await _acp.CloseAsync(sessionId, ct);
    }
}
