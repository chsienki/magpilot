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
    private readonly HostOwnership _hostOwnership;
    private readonly ILogger<SessionRegistry> _logger;
    private readonly ConcurrentDictionary<string, byte> _owned = new();

    public SessionRegistry(AcpSessionManager acp, SessionScanner scanner, HostOwnership hostOwnership, ILogger<SessionRegistry> logger)
    {
        _acp = acp;
        _scanner = scanner;
        _hostOwnership = hostOwnership;
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

    /// <summary>
    /// Compute the rich ownership + activity view of a session for the
    /// magpilot launcher or SPA. Cheap: filesystem stat + a few
    /// in-memory lookups + an events.jsonl tail read.
    /// </summary>
    public SessionStateInfo? GetState(string sessionId)
    {
        var info = _scanner.Get(sessionId, Owned);
        if (info is null) return null;

        SessionOwner owner;
        int? hostPid = null;
        if (_hostOwnership.TryGet(sessionId, out var hostEntry))
        {
            owner = SessionOwner.Host;
            hostPid = hostEntry.HostPid;
        }
        else if (_owned.ContainsKey(sessionId))
        {
            owner = SessionOwner.Agent;
        }
        else if (info.OwnerPid is int ownerPid && IsAlive(ownerPid))
        {
            owner = SessionOwner.External;
        }
        else
        {
            owner = SessionOwner.None;
        }

        SessionActivity activity;
        InFlightInfo? inFlight = null;
        if (_acp.IsTurnInFlight(sessionId, out var entry))
        {
            activity = SessionActivity.InFlight;
            inFlight = new InFlightInfo(
                Driver: entry.Requester,
                StartedAtMs: entry.StartedAt.ToUnixTimeMilliseconds(),
                Preview: null);
        }
        else
        {
            activity = SessionActivity.Idle;
        }

        var lastEvent = TryReadLastEvent(sessionId);

        return new SessionStateInfo(info, owner, hostPid, activity, inFlight, lastEvent);
    }

    /// <summary>
    /// Hand off a session to a magpilot launcher. Atomic combined op:
    /// waits for any in-flight turn to reach a clean boundary (or aborts
    /// it if <paramref name="force"/> is true), drops our agent-side
    /// ownership, and records the host as the new owner.
    /// </summary>
    public async Task<SessionStateInfo> AcquireForHostAsync(string sessionId, int hostPid, bool force, CancellationToken ct)
    {
        if (_scanner.Get(sessionId, Owned) is null)
            throw new FileNotFoundException($"Session {sessionId} not on disk");

        // If we currently own the session, gracefully release it.
        if (_owned.ContainsKey(sessionId))
        {
            if (_acp.IsTurnInFlight(sessionId, out _))
            {
                if (force)
                {
                    _logger.LogInformation("AcquireForHost (force): cancelling in-flight turn on {Sid}", sessionId);
                    try { await _acp.CancelAsync(sessionId, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "session/cancel failed during force-acquire for {Sid}", sessionId); }
                    // Give the turn ~2s to finalize (it should emit TurnComplete).
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    grace.CancelAfter(TimeSpan.FromSeconds(2));
                    try { await _acp.WaitForTurnBoundaryAsync(sessionId, grace.Token); }
                    catch (OperationCanceledException) { /* stop waiting; we'll detach anyway */ }
                }
                else
                {
                    _logger.LogInformation("AcquireForHost (polite): waiting for in-flight turn on {Sid}", sessionId);
                    await _acp.WaitForTurnBoundaryAsync(sessionId, ct);
                }
            }
            await DetachAsync(sessionId, ct);
        }

        _hostOwnership.Set(sessionId, hostPid);

        // Refresh state for the response (now reflecting host ownership).
        return GetState(sessionId)!;
    }

    /// <summary>
    /// The host's wrapper has shut down its child copilot cleanly and is
    /// handing the session back to the agent. We re-load it into our ACP
    /// child and clear the host-ownership marker.
    /// </summary>
    public async Task<SessionStateInfo> ReleaseFromHostAsync(string sessionId, int hostPid, CancellationToken ct)
    {
        var info = _scanner.Get(sessionId, Owned)
            ?? throw new FileNotFoundException($"Session {sessionId} not on disk");

        if (_hostOwnership.TryGet(sessionId, out var entry) && entry.HostPid != hostPid)
            throw new InvalidOperationException(
                $"Session is held by host PID {entry.HostPid}, not {hostPid}; cannot release on its behalf.");

        _hostOwnership.Clear(sessionId);

        // Re-load the session into our ACP child. We don't kill any lock
        // holders -- the inuse.<pid>.lock files are advisory. session/load
        // re-reads events.jsonl from disk so we pick up everything the
        // host's interactive copilot appended while it was driving.
        //
        // Caveats with the current copilot --acp:
        //   * It doesn't implement session/close (Method not found), so we
        //     can't politely evict before reloading.
        //   * session/load on an already-in-memory session returns
        //     "Session is already loaded" -- it does NOT re-read disk.
        // The session may therefore be stale in the multiplex child's memory
        // by the events the host appended. We mark _owned anyway so callers
        // can route prompts; the staleness window is documented and can be
        // fixed in a future phase by respawning the default-flavor child
        // when host hand-back is detected.
        var cwd = info.Cwd ?? Environment.CurrentDirectory;
        try
        {
            await _acp.LoadSessionAsync(sessionId, cwd, AcpFlavor.Default, ct);
            _owned.TryAdd(sessionId, 0);
        }
        catch (Exception ex) when (ex.Message.Contains("already loaded", StringComparison.OrdinalIgnoreCase))
        {
            // Already in memory -- expected when the multiplex child was
            // never told to evict (because session/close isn't supported).
            // Mark _owned so the agent knows it can route prompts here.
            _logger.LogDebug("session/load returned 'already loaded' for {Sid} on release; marking owned anyway", sessionId);
            _owned.TryAdd(sessionId, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session/load failed during ReleaseFromHost for {Sid}", sessionId);
            // Don't add to _owned; subsequent calls can retry.
        }

        return GetState(sessionId)!;
    }

    private LastEventInfo? TryReadLastEvent(string sessionId)
    {
        try
        {
            var path = Path.Combine(_scanner.Root, sessionId, "events.jsonl");
            if (!File.Exists(path)) return null;
            // Tail the last line. Cheap for a few-MB file; we read the
            // whole thing only if it's small. For large files we seek
            // from the end.
            var fi = new FileInfo(path);
            string? lastLine = null;
            if (fi.Length < 64 * 1024)
            {
                lastLine = File.ReadLines(path).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            }
            else
            {
                using var fs = File.OpenRead(path);
                fs.Seek(-Math.Min(64 * 1024, fs.Length), SeekOrigin.End);
                using var sr = new StreamReader(fs);
                _ = sr.ReadLine(); // discard partial first line
                string? l;
                while ((l = sr.ReadLine()) != null)
                    if (!string.IsNullOrWhiteSpace(l)) lastLine = l;
            }
            if (lastLine is null) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(lastLine);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
            var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            DateTimeOffset? ts = null;
            if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed))
                ts = parsed;
            return new LastEventInfo(type, id, ts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryReadLastEvent failed for {Sid}", sessionId);
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }
}
