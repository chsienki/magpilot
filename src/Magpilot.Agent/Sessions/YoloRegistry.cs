using System.Collections.Concurrent;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Per-session "yolo mode" flag. When a session is yolo-enabled, the
/// agent auto-approves every <c>session/request_permission</c> callback
/// for it (picking an allow-flavored option), the same way the
/// env-wide <c>MAGPILOT_AUTO_APPROVE=true</c> short-circuits the SSE
/// approval round-trip -- but scoped to one session that the user
/// explicitly opted in via the SPA toggle.
///
/// State is in-memory only: not written to <c>workspace.yaml</c>
/// (the Copilot CLI owns that file), not survived across agent
/// restarts. Explicit opt-in stays explicit: a restarted agent
/// defaults every session to safe.
///
/// Host-level kill switch: <c>MAGPILOT_YOLO_DISABLED=true</c> in the
/// agent's environment makes <see cref="HostDisabled"/> true; the
/// HTTP endpoint refuses every <see cref="Set"/> with 403 and
/// <see cref="IsEnabled"/> always returns false. Intended for
/// user-account agents like HENDRIK where the agent runs with the
/// user's full permissions and unattended auto-approve would be
/// dangerous.
/// </summary>
public sealed class YoloRegistry
{
    private readonly ILogger<YoloRegistry> _logger;
    private readonly ConcurrentDictionary<string, byte> _enabled = new();

    public YoloRegistry(ILogger<YoloRegistry> logger)
    {
        _logger = logger;
        HostDisabled = string.Equals(
            Environment.GetEnvironmentVariable("MAGPILOT_YOLO_DISABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (HostDisabled)
            _logger.LogInformation("YoloRegistry: per-session yolo mode is DISABLED on this host (MAGPILOT_YOLO_DISABLED=true)");
    }

    /// <summary>
    /// True if the agent's host has set <c>MAGPILOT_YOLO_DISABLED=true</c>.
    /// When set, <see cref="IsEnabled"/> always returns false and the
    /// HTTP endpoint refuses every flip request with 403.
    /// </summary>
    public bool HostDisabled { get; }

    /// <summary>
    /// Is yolo currently enabled for this session? Always false when
    /// <see cref="HostDisabled"/> is true, regardless of any prior Set.
    /// </summary>
    public bool IsEnabled(string sessionId)
    {
        if (HostDisabled) return false;
        return _enabled.ContainsKey(sessionId);
    }

    /// <summary>
    /// Enable or disable yolo for this session. Returns the new state
    /// (always false when <see cref="HostDisabled"/>; callers should
    /// have refused at the HTTP layer in that case but this method
    /// is defensive anyway).
    /// </summary>
    public bool Set(string sessionId, bool enabled)
    {
        if (HostDisabled) return false;
        if (enabled)
        {
            _enabled[sessionId] = 0;
            _logger.LogInformation("Yolo ENABLED for session {Sid}", sessionId);
            return true;
        }
        if (_enabled.TryRemove(sessionId, out _))
            _logger.LogInformation("Yolo DISABLED for session {Sid}", sessionId);
        return false;
    }

    /// <summary>
    /// Drop the yolo bit without logging at Information level. Used by
    /// internal lifecycle hooks (e.g. host-acquire) where the change
    /// is incidental, not a user decision.
    /// </summary>
    public void Clear(string sessionId)
    {
        if (_enabled.TryRemove(sessionId, out _))
            _logger.LogDebug("Yolo cleared (lifecycle) for session {Sid}", sessionId);
    }
}
