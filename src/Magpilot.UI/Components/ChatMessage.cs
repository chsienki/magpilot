namespace Magpilot.UI.Components;

public sealed class ChatMessage
{
    public required string Role { get; init; }
    public string Text { get; set; } = "";
    public string? ToolCallId { get; init; }
}
