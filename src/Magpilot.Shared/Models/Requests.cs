namespace Magpilot.Shared.Models;

public sealed record NewSessionRequest(
    string? Cwd,
    string? Name,
    string? InitialPrompt = null,
    bool UseAgency = false);
public sealed record PromptRequest(string Text);
public sealed record AdoptRequest(bool Force = false);
public sealed record ApprovalResponse(string OptionId);

public sealed record SessionDetails(SessionInfo Info, string? AcpSessionId);

/// <summary>
/// Body for <c>POST /api/sessions/{id}/release-request</c>. Triggers a
/// SSE <c>release_requested</c> event so any magpilot-host wrapper that
/// owns the session can begin its graceful wind-down.
/// </summary>
public sealed record ReleaseRequestBody(
    /// <summary>Free-form label identifying who's asking (e.g. "spa", "whatsapp", "cron").</summary>
    string Requester,
    /// <summary>If true, the requester would prefer the host abort its in-flight turn rather than wait.</summary>
    bool Force = false);

/// <summary>
/// Body for <c>POST /api/sessions/{id}/acquire-for-host</c>. The wrapper
/// supplies its own PID so the agent can verify liveness later. If the
/// agent currently owns the session and a turn is in flight, the agent
/// waits for the next turn boundary unless <see cref="Force"/> is true,
/// in which case it sends an ACP cancel and proceeds after a short
/// grace period.
/// </summary>
public sealed record AcquireForHostBody(int HostPid, bool Force = false);

/// <summary>
/// Body for <c>POST /api/sessions/{id}/release</c>. The wrapper calls
/// this after its child copilot has exited cleanly, signalling that
/// the agent may re-adopt the session.
/// </summary>
public sealed record ReleaseFromHostBody(int HostPid);

/// <summary>
/// Response body returned by ACP-driving endpoints (<c>POST /messages</c>,
/// <c>POST /interrupt</c>, <c>POST /approvals/{id}</c>) with status code
/// <c>409 Conflict</c> when the session is currently held by a
/// magpilot-host wrapper. Callers (SPA, WhatsApp) react by firing
/// <c>POST /release-request</c> and polling <c>GET /state</c> until the
/// host releases, then retrying the original POST.
/// </summary>
public sealed record HostOwnedResponse(
    string Error,
    bool NeedsRelease,
    int HostPid);

/// <summary>
/// Request body for the synchronous "ask Copilot a question and wait for the answer"
/// endpoint. Hub creates an ephemeral session, sends the prompt, accumulates the
/// streamed assistant output, and returns it as a single response.
///
/// If <see cref="SessionId"/> is supplied, the call reuses an existing session
/// instead of creating a new ephemeral one. The session is NOT detached after
/// the call (regardless of <see cref="KeepSession"/>) so caller-pinned
/// long-lived sessions survive across many quick-prompt calls.
/// </summary>
public sealed record QuickPromptRequest(
    string Prompt,
    string? Cwd = null,
    int? TimeoutSeconds = null,
    bool KeepSession = false,
    string? SessionId = null);

/// <summary>
/// Response body for /quick-prompt. <see cref="ResponseText"/> is the concatenation
/// of every AssistantDelta chunk emitted during the turn. <see cref="StopReason"/>
/// reflects the final TurnComplete event ("end_turn", "max_tokens", etc.).
/// <see cref="SessionId"/> is the underlying session id; if KeepSession was false
/// the session has been detached (but the on-disk events.jsonl remains for replay).
/// </summary>
public sealed record QuickPromptResponse(
    string ResponseText,
    string StopReason,
    string SessionId);
