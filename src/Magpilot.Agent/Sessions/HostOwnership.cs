using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Tracks which sessions are currently held by a magpilot launcher
/// process (i.e. an interactive copilot session that the user is
/// driving in a terminal, with the wrapper coordinating with this
/// agent).
///
/// This is the authority for "is this session being driven by a host?"
/// The on-disk inuse.&lt;pid&gt;.lock files are advisory only; they don't
/// form a true mutex (two PIDs can claim the same session simultaneously
/// and the file system does nothing to prevent it). So we keep our own
/// ownership map and consult it before any agent code path tries to
/// drive a session.
///
/// The map is mirrored to a small JSON file and reloaded on startup so
/// an agent restart -- which happens on every re-pair and every update --
/// does NOT orphan the sessions a launcher is still driving. On load,
/// each entry is revalidated against the live process (PID alive AND its
/// start time matches the one captured at acquire, to defeat PID reuse);
/// stale entries are dropped. A small background sweep prunes entries
/// whose holder dies mid-run.
/// </summary>
public sealed class HostOwnership : IHostedService, IDisposable
{
    private readonly ILogger<HostOwnership> _logger;
    private readonly ConcurrentDictionary<string, HostOwnerEntry> _entries = new();
    private readonly string _statePath;
    private readonly object _persistLock = new();
    private Timer? _sweep;

    public HostOwnership(ILogger<HostOwnership> logger, IConfiguration? config = null)
    {
        _logger = logger;
        _statePath = ResolveStatePath(config);
    }

    /// <summary>
    /// Where the ownership map is mirrored. Overridable via
    /// <c>Agent:HostOwnershipFile</c> / <c>MAGPILOT_HOSTOWNERSHIP_FILE</c>
    /// (used by tests); defaults next to the copilot session state under
    /// the user profile so it travels with the machine, not the install.
    /// </summary>
    private static string ResolveStatePath(IConfiguration? config)
    {
        var explicitPath = config?["Agent:HostOwnershipFile"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_HOSTOWNERSHIP_FILE");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "magpilot", "hostownership.json");
    }

    /// <summary>
    /// Mark the session as host-owned. Replaces any prior entry; the
    /// caller is responsible for ensuring the agent has already released
    /// any in-flight ACP work for this session before calling. The
    /// holder's process start time is captured so a reload after an agent
    /// restart can tell the real holder from a reused PID.
    /// </summary>
    public void Set(string sessionId, int hostPid)
    {
        _entries[sessionId] = new HostOwnerEntry(hostPid, DateTimeOffset.UtcNow, TryGetStartTicks(hostPid));
        _logger.LogInformation("Host {Pid} acquired session {Sid}", hostPid, sessionId);
        Persist();
    }

    /// <summary>Drop the host-ownership marker (e.g. when the host releases).</summary>
    public bool Clear(string sessionId)
    {
        if (_entries.TryRemove(sessionId, out var entry))
        {
            _logger.LogInformation("Host {Pid} released session {Sid}", entry.HostPid, sessionId);
            Persist();
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
            if (_entries.TryRemove(sessionId, out _)) Persist();
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

    /// <summary>
    /// Process start time in UTC ticks, or 0 if it can't be read. Used as a
    /// PID-reuse guard across an agent restart: a different process that
    /// later reuses the same PID will have a different start time.
    /// </summary>
    private static long TryGetStartTicks(int pid)
    {
        try { return Process.GetProcessById(pid).StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Load();
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
        var changed = false;
        foreach (var (sid, entry) in _entries.ToArray())
        {
            if (!IsAlive(entry.HostPid) && _entries.TryRemove(sid, out _))
            {
                changed = true;
                _logger.LogInformation("Swept stale host entry sid={Sid} pid={Pid}", sid, entry.HostPid);
            }
        }
        if (changed) Persist();
    }

    /// <summary>
    /// Reload the persisted map, keeping only entries whose holder is still
    /// the same live process (PID alive AND start time matches when known).
    /// This is what lets a launcher-driven session survive an agent restart.
    /// Exposed internally so a test can drive load without the hosted-service
    /// timer.
    /// </summary>
    internal void Load()
    {
        List<PersistedEntry>? saved;
        try
        {
            if (!File.Exists(_statePath)) return;
            saved = JsonSerializer.Deserialize<List<PersistedEntry>>(File.ReadAllText(_statePath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read host-ownership state {Path}; starting empty", _statePath);
            return;
        }
        if (saved is null) return;

        var kept = 0;
        foreach (var e in saved)
        {
            if (string.IsNullOrEmpty(e.SessionId) || !IsAlive(e.HostPid)) continue;
            // PID-reuse guard: if we recorded a start time, the live process
            // must still have it. A 0 means "unknown at capture" -- accept on
            // liveness alone rather than drop a genuine holder.
            if (e.HostStartTicks != 0 && TryGetStartTicks(e.HostPid) != e.HostStartTicks) continue;
            _entries[e.SessionId] = new HostOwnerEntry(e.HostPid, e.AcquiredAt, e.HostStartTicks);
            kept++;
        }
        if (kept > 0)
            _logger.LogInformation("Reloaded {Kept}/{Total} host-owned session(s) after restart", kept, saved.Count);
        // Rewrite so the file reflects the pruned set.
        Persist();
    }

    private void Persist()
    {
        try
        {
            var snapshot = _entries
                .Select(kv => new PersistedEntry(kv.Key, kv.Value.HostPid, kv.Value.AcquiredAt, kv.Value.HostStartTicks))
                .ToList();
            var json = JsonSerializer.Serialize(snapshot);
            lock (_persistLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _statePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            // Persistence is best-effort: a write failure must never break
            // ownership tracking, it just costs durability across a restart.
            _logger.LogWarning(ex, "Could not persist host-ownership state to {Path}", _statePath);
        }
    }

    public void Dispose() => _sweep?.Dispose();

    private sealed record PersistedEntry(string SessionId, int HostPid, DateTimeOffset AcquiredAt, long HostStartTicks);
}

public readonly record struct HostOwnerEntry(int HostPid, DateTimeOffset AcquiredAt, long HostStartTicks);
