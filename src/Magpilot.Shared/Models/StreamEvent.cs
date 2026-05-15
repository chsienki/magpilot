using System.Text.Json.Serialization;

namespace Magpilot.Shared.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AssistantDelta), "assistant_delta")]
[JsonDerivedType(typeof(ThoughtDelta), "thought_delta")]
[JsonDerivedType(typeof(UserDelta), "user_delta")]
[JsonDerivedType(typeof(ToolCallStart), "tool_call_start")]
[JsonDerivedType(typeof(ToolCallProgress), "tool_call_progress")]
[JsonDerivedType(typeof(ToolCallEnd), "tool_call_end")]
[JsonDerivedType(typeof(ApprovalRequired), "approval_required")]
[JsonDerivedType(typeof(TurnComplete), "turn_complete")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(HeartbeatEvent), "heartbeat")]
[JsonDerivedType(typeof(LoadStarted), "load_started")]
[JsonDerivedType(typeof(HistoryDone), "history_done")]
[JsonDerivedType(typeof(LoadFailed), "load_failed")]
[JsonDerivedType(typeof(ReleaseRequested), "release_requested")]
public abstract record StreamEvent;

public sealed record AssistantDelta(string Text) : StreamEvent;
public sealed record ThoughtDelta(string Text) : StreamEvent;
public sealed record UserDelta(string Text) : StreamEvent;
public sealed record ToolCallStart(string ToolCallId, string Name, string? RawInput) : StreamEvent;
public sealed record ToolCallProgress(string ToolCallId, string? PartialOutput) : StreamEvent;
public sealed record ToolCallEnd(string ToolCallId, string? Result, bool Success) : StreamEvent;
public sealed record ApprovalRequired(string ApprovalId, string Title, string? Detail, IReadOnlyList<ApprovalOption> Options) : StreamEvent;
public sealed record TurnComplete(string StopReason) : StreamEvent;
public sealed record ErrorEvent(string Message) : StreamEvent;
public sealed record HeartbeatEvent : StreamEvent;
public sealed record LoadStarted : StreamEvent;
public sealed record HistoryDone : StreamEvent;
public sealed record LoadFailed(string Error) : StreamEvent;

/// <summary>
/// Notification fired by the agent when something (the SPA, WhatsApp,
/// the cron sidecar, ...) wants to drive a session that a magpilot
/// launcher currently owns. The launcher is expected to gracefully wind
/// down its child copilot process and POST /api/sessions/{id}/release so
/// the agent can take over.
/// </summary>
/// <param name="Requester">Free-form label identifying who's asking (e.g. "spa", "whatsapp", "cron").</param>
/// <param name="Force">If true, the requester would prefer the host abort its in-flight turn rather than wait for it to complete.</param>
public sealed record ReleaseRequested(string Requester, bool Force) : StreamEvent;

public sealed record ApprovalOption(string OptionId, string Label, string? Kind);
