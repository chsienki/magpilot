using System.Collections.Concurrent;
using System.Diagnostics;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Tracks which sessions are currently held by a magpilot launcher
/// process (i.e. an interactive copilot session that the user is
/// driving in a terminal, with the wrapper coordinating with this
/// agent).
///
/// This is the in-memory authority for "is this session being driven by
/// a host?" The on-disk inuse.&lt;pid&gt;.lock files are advisory only;
/// they don't form a true mutex (two PIDs can claim the same session
/// simultaneously and the file system does nothing to prevent it). So
/// we keep our own ownership map and consult it before any agent code
/// path tries to drive a session.
///
/// Entries auto-expire when the host PID is no longer alive: a small
/// background sweep removes stale entries every few seconds so a
/// wrapper that crashed or was kill -9'd doesn't leave the session
/// stuck.
/// </summary>
public sealed class HostOwnership : IHostedService, IDisposable
{
    private readonly ILogger<HostOwnership> _logger;
    private readonly ConcurrentDictionary<string, HostOwnerEntry> _entries = new();
    private Timer? _sweep;

    public HostOwnership(ILogger<HostOwnership> logger) => _logger = logger;

    /// <summary>
    /// Mark the session as host-owned. Replaces any prior entry; the
    /// caller is responsible for ensuring the agent has already released
    /// any in-flight ACP work for this session before calling.
    /// </summary>
    public void Set(string sessionId, int hostPid)
    {
        _entries[sessionId] = new HostOwnerEntry(hostPid, DateTimeOffset.UtcNow);
        _logger.LogInformation("Host {Pid} acquired session {Sid}", hostPid, sessionId);
    }

    /// <summary>Drop the host-ownership marker (e.g. when the host releases).</summary>
    public bool Clear(string sessionId)
    {
        if (_entries.TryRemove(sessionId, out var entry))
        {
            _logger.LogInformation("Host {Pid} released session {Sid}", entry.HostPid, sessionId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Check whether the session is currently held by a host. If yes,
    /// also verifies the host PID is still alive; if not, drops the
    /// entry and returns false.
    /// </summary>
    public bool TryGet(string sessionId, out HostOwnerEntry entry)
    {
        if (_entries.TryGetValue(sessionId, out entry!))
        {
            if (IsAlive(entry.HostPid))
                return true;
            // Stale entry -- holder process is gone. Clean up.
            _entries.TryRemove(sessionId, out _);
            _logger.LogWarning("Pruned stale host entry sid={Sid} pid={Pid}", sessionId, entry.HostPid);
        }
        entry = default!;
        return false;
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Periodic sweep so dead hosts don't accumulate (e.g. user
        // kill -9'd their wrapper or rebooted with a session held).
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sweep?.Dispose();
        _sweep = null;
        return Task.CompletedTask;
    }

    private void Sweep()
    {
        foreach (var (sid, entry) in _entries.ToArray())
        {
            if (!IsAlive(entry.HostPid))
            {
                _entries.TryRemove(sid, out _);
                _logger.LogInformation("Swept stale host entry sid={Sid} pid={Pid}", sid, entry.HostPid);
            }
        }
    }

    public void Dispose() => _sweep?.Dispose();
}

public readonly record struct HostOwnerEntry(int HostPid, DateTimeOffset AcquiredAt);
