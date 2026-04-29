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

    public async Task<SessionInfo> CreateAsync(string? cwd, CancellationToken ct)
    {
        cwd ??= Environment.CurrentDirectory;
        var sid = await _acp.NewSessionAsync(cwd, ct);
        _owned.TryAdd(sid, 0);
        return _scanner.Get(sid, Owned)
            ?? new SessionInfo(sid, SessionState.Owned, cwd, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Adopt: if the session is held by another process, kill it (force=true required),
    /// then session/load it into our ACP child.
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
        await _acp.LoadSessionAsync(sessionId, cwd, ct);
        _owned.TryAdd(sessionId, 0);
        return _scanner.Get(sessionId, Owned) ?? info with { State = SessionState.Owned };
    }

    public async Task DetachAsync(string sessionId, CancellationToken ct)
    {
        if (_owned.TryRemove(sessionId, out _))
            await _acp.CloseAsync(sessionId, ct);
    }
}
