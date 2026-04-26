using System.Text.Json.Serialization;

namespace Clawpilot.Shared.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AssistantDelta), "assistant_delta")]
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
public abstract record StreamEvent;

public sealed record AssistantDelta(string Text) : StreamEvent;
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

public sealed record ApprovalOption(string OptionId, string Label, string? Kind);
