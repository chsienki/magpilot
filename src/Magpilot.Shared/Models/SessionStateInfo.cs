namespace Magpilot.Shared.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Detailed view of a single session's current ownership and activity,
/// returned by <c>GET /api/sessions/{id}/state</c>. Used by the
/// magpilot launcher to decide whether to prompt the user before
/// taking over a session, and by the SPA's session list to render
/// a richer "what's happening" badge per row.
/// </summary>
/// <remarks>
/// This is a SUPERSET of <see cref="SessionInfo"/>. The wire format
/// includes the underlying <see cref="SessionInfo"/> so older clients
/// that just want the basic fields can still parse it.
/// </remarks>
public sealed record SessionStateInfo(
    SessionInfo Info,
    SessionOwner Owner,
    int? HostPid,
    SessionActivity Activity,
    InFlightInfo? InFlight,
    LastEventInfo? LastEvent
);

/// <summary>
/// Who's currently driving the session, from the agent's perspective.
/// The on-disk inuse.&lt;pid&gt;.lock files are advisory only -- this
/// owner field is the agent's authoritative view based on its own
/// in-memory tracking.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionOwner>))]
public enum SessionOwner
{
    /// <summary>No-one currently driving; safe for any caller to acquire.</summary>
    None,
    /// <summary>This magpilot agent's copilot --acp child is driving via ACP.</summary>
    Agent,
    /// <summary>A magpilot launcher has acquired the session for a local terminal.</summary>
    Host,
    /// <summary>Some other process holds an inuse.lock that we don't track (e.g. a raw <c>copilot</c> in a terminal).</summary>
    External,
}

[JsonConverter(typeof(JsonStringEnumConverter<SessionActivity>))]
public enum SessionActivity
{
    /// <summary>No turn in flight on the agent's side.</summary>
    Idle,
    /// <summary>Agent is mid-turn for this session.</summary>
    InFlight,
    /// <summary>Last turn completed within the past few seconds (for grace-period UX).</summary>
    JustFinished,
}

/// <summary>
/// Snapshot of the in-flight turn for a session, when one is in progress
/// on the agent's side. Used by the wrapper to render a meaningful
/// "agent is busy because..." line in its take-over prompt.
/// </summary>
public sealed record InFlightInfo(
    string? Driver,
    long StartedAtMs,
    string? Preview
);

/// <summary>
/// Tail snapshot of the session's events.jsonl: the most recent event's
/// type, id, and timestamp. Used by the wrapper to show "what was the
/// last thing that happened?" on the take-over prompt without having to
/// download the whole history.
/// </summary>
public sealed record LastEventInfo(
    string Type,
    string? Id,
    DateTimeOffset? Timestamp
);
