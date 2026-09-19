using GitHub.Copilot;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Runtime.Sdk;

/// <summary>
/// Maps one active SDK turn onto Magpilot's existing SSE contract.
/// </summary>
internal sealed class SdkTurnEventMapper
{
    private bool _active;
    private bool _terminalPublished;

    public void BeginTurn()
    {
        _active = true;
        _terminalPublished = false;
    }

    public IReadOnlyList<StreamEvent> Map(SessionEvent evt)
    {
        if (!_active)
            return [];

        return evt switch
        {
            AssistantMessageDeltaEvent message =>
                [new AssistantDelta(message.Data.DeltaContent ?? "")],
            AssistantReasoningDeltaEvent reasoning =>
                [new ThoughtDelta(reasoning.Data.DeltaContent ?? "")],
            ToolExecutionStartEvent tool =>
                [MapToolStart(tool.Data)],
            ToolExecutionProgressEvent progress =>
                [new ToolCallProgress(
                    progress.Data.ToolCallId ?? "",
                    progress.Data.ProgressMessage)],
            ToolExecutionPartialResultEvent partial =>
                [new ToolCallProgress(
                    partial.Data.ToolCallId ?? "",
                    partial.Data.PartialOutput)],
            ToolExecutionCompleteEvent completed =>
                [MapToolComplete(completed.Data)],
            SessionErrorEvent error =>
                CompleteWithError(error.Data.Message),
            SessionIdleEvent idle =>
                CompleteIdle(idle.Data.Aborted == true),
            _ => [],
        };
    }

    public IReadOnlyList<StreamEvent> Fail(string message) =>
        _active ? CompleteWithError(message) : [];

    private IReadOnlyList<StreamEvent> CompleteWithError(string? message)
    {
        if (_terminalPublished)
            return [];

        _terminalPublished = true;
        _active = false;
        return
        [
            new ErrorEvent(message ?? "The Copilot runtime reported an error."),
            new TurnComplete("error"),
        ];
    }

    private IReadOnlyList<StreamEvent> CompleteIdle(bool aborted)
    {
        if (_terminalPublished)
            return [];

        _terminalPublished = true;
        _active = false;
        return [new TurnComplete(aborted ? "cancelled" : "end_turn")];
    }

    private static ToolCallStart MapToolStart(ToolExecutionStartData data)
    {
        var name = data.ToolDescription?.Name
            ?? data.ToolName
            ?? "tool";
        var rawInput = data.Arguments is { } arguments
            ? arguments.GetRawText()
            : null;
        return new ToolCallStart(data.ToolCallId ?? "", name, rawInput);
    }

    private static ToolCallEnd MapToolComplete(ToolExecutionCompleteData data)
    {
        var result = data.Success
            ? data.Result?.DetailedContent
                ?? data.Result?.Content
                ?? data.Result?.StructuredContent?.GetRawText()
            : data.Error?.Message;
        return new ToolCallEnd(
            data.ToolCallId ?? "",
            result,
            data.Success);
    }
}
