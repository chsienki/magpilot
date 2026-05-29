namespace Magpilot.UI.Components;

public sealed class ChatMessage
{
    public required string Role { get; init; }
    public string Text { get; set; } = "";
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Lifecycle state of a tool call. Always <see cref="ToolStatus.Pending"/>
    /// for non-tool messages (the field is just ignored for those).
    /// Mutates from Pending to Ok / Fail when the matching
    /// <c>tool_call_end</c> event arrives, so the same chip animates in
    /// place instead of leaving a separate "[end] ok" sibling behind.
    /// </summary>
    public ToolStatus ToolStatus { get; set; } = ToolStatus.Pending;
}

public enum ToolStatus
{
    Pending,
    Ok,
    Fail,
}
