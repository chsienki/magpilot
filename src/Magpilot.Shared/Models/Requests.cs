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
/// Request body for the synchronous "ask Copilot a question and wait for the answer"
/// endpoint. Hub creates an ephemeral session, sends the prompt, accumulates the
/// streamed assistant output, and returns it as a single response.
/// </summary>
public sealed record QuickPromptRequest(
    string Prompt,
    string? Cwd = null,
    int? TimeoutSeconds = null,
    bool KeepSession = false);

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
