namespace Magpilot.Hub.Logging;

/// <summary>
/// One row in the central log. Sent by the SPA, the hub itself, the agents,
/// and the sidecars via <c>POST /api/log</c>. The hub owns the timestamp +
/// id assignment; everything else is caller-supplied.
/// </summary>
/// <param name="Source">
/// Free-form origin label. Conventions: <c>spa</c>, <c>hub</c>, agent name
/// (e.g. <c>magnus</c>, <c>HENDRIK</c>), sidecar name (e.g.
/// <c>whatsapp</c>, <c>cron</c>).
/// </param>
/// <param name="Level">
/// One of: <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>,
/// <c>Critical</c>. Anything else is stored verbatim but is hard to filter on.
/// </param>
/// <param name="Category">Logger category / module name; optional.</param>
/// <param name="Message">Human-readable message; required.</param>
/// <param name="Stack">Stack trace if available; optional.</param>
/// <param name="SessionId">Magpilot session id this event relates to; optional.</param>
/// <param name="Extra">Free-form JSON properties; optional.</param>
/// <param name="UserAgent">Browser UA for SPA events; optional.</param>
/// <param name="Url">Page URL for SPA events; optional.</param>
public sealed record LogEventDto(
    string Source,
    string Level,
    string? Category,
    string Message,
    string? Stack,
    string? SessionId,
    System.Text.Json.JsonElement? Extra,
    string? UserAgent,
    string? Url);

/// <summary>
/// One row as returned to the viewer. Adds the hub-assigned id and timestamp.
/// </summary>
public sealed record LogEventRow(
    long Id,
    DateTimeOffset Timestamp,
    string Source,
    string Level,
    string? Category,
    string Message,
    string? Stack,
    string? SessionId,
    string? Extra,
    string? UserAgent,
    string? Url);

/// <summary>
/// Optional batched ingest -- callers can buffer locally to amortise HTTP cost
/// (e.g. SPA flushing on navigate-away, agents flushing once a second).
/// </summary>
public sealed record LogEventBatch(IReadOnlyList<LogEventDto> Events);

/// <summary>
/// Filter for the read endpoint. All fields optional.
/// </summary>
public sealed record LogQuery(
    string? Source,
    string? Level,
    string? SessionId,
    string? Search,
    DateTimeOffset? Since,
    int? Limit);
