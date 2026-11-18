#pragma warning disable GHCP001
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
    private bool _fusionUncommitted;
    private readonly HashSet<string> _streamedMessages = [];
    private readonly HashSet<string> _completedMessages = [];
    private readonly HashSet<string> _provisionalMessages = [];
    private readonly HashSet<string> _startedTools = [];
    private readonly HashSet<string> _completedTools = [];

    public void BeginTurn()
    {
        _active = true;
        _terminalPublished = false;
        _fusionUncommitted = false;
        _streamedMessages.Clear();
        _completedMessages.Clear();
        _provisionalMessages.Clear();
        _startedTools.Clear();
        _completedTools.Clear();
    }

    public IReadOnlyList<StreamEvent> Map(SessionEvent evt)
    {
        if (!_active)
            return [];

        return evt switch
        {
            AssistantMessageDeltaEvent message =>
                MapMessageDelta(message.Data),
            AssistantMessageEvent message =>
                MapCompletedMessage(message.Data),
            AssistantFusionPhaseStartedEvent phase =>
                StartFusionPhase(phase.Data),
            AssistantReasoningDeltaEvent reasoning =>
                [new ThoughtDelta(reasoning.Data.DeltaContent ?? "")],
            ToolExecutionStartEvent tool =>
                FirstForTool(_startedTools, tool.Data.ToolCallId, MapToolStart(tool.Data)),
            ToolExecutionProgressEvent progress =>
                [new ToolCallProgress(
                    progress.Data.ToolCallId ?? "",
                    progress.Data.ProgressMessage)],
            ToolExecutionPartialResultEvent partial =>
                [new ToolCallProgress(
                    partial.Data.ToolCallId ?? "",
                    partial.Data.PartialOutput)],
            ToolExecutionCompleteEvent completed =>
                FirstForTool(_completedTools, completed.Data.ToolCallId, MapToolComplete(completed.Data)),
            SessionErrorEvent error =>
                CompleteWithError(error.Data.Message),
            SessionIdleEvent idle =>
                CompleteIdle(idle.Data.Aborted == true),
            _ => [],
        };
    }

    public IReadOnlyList<StreamEvent> Fail(string message) =>
        _active ? CompleteWithError(message) : [];

    private IReadOnlyList<StreamEvent> StartFusionPhase(AssistantFusionPhaseStartedData data)
    {
        // A single-pattern phase is always the committed answer, so there is
        // no candidate for a consumer to replace.
        _fusionUncommitted = data.Pattern != FusionPattern.Single;
        return [];
    }

    private IReadOnlyList<StreamEvent> MapMessageDelta(AssistantMessageDeltaData data)
    {
        if (!string.IsNullOrEmpty(data.DeltaContent))
        {
            _streamedMessages.Add(data.MessageId);
            if (_fusionUncommitted)
                _provisionalMessages.Add(data.MessageId);
        }
        // Deltas carry no Fusion attribution, so phase state decides whether
        // they belong to a candidate that a later commit may replace.
        return [new AssistantDelta(data.DeltaContent ?? "", _fusionUncommitted)];
    }

    private IReadOnlyList<StreamEvent> MapCompletedMessage(AssistantMessageData data)
    {
        var fusion = data.Fusion;
        if (fusion is not null &&
            string.Equals(fusion.ConversationScope, "review", StringComparison.OrdinalIgnoreCase))
            return [];

        var committed = !string.IsNullOrEmpty(fusion?.CommitId);
        if (committed)
        {
            _fusionUncommitted = false;
            // The commit can reuse the candidate's message ID; consumers holding
            // provisional text still need the committed copy.
            if (_provisionalMessages.Remove(data.MessageId) && !string.IsNullOrEmpty(data.Content))
                return [new AssistantDelta(data.Content)];
        }

        // Fusion can commit canonical output without streaming root-message deltas.
        if (_streamedMessages.Contains(data.MessageId) ||
            !_completedMessages.Add(data.MessageId) ||
            string.IsNullOrEmpty(data.Content))
            return [];

        // Fusion output without a commit ID is a candidate (draft, primary, or
        // repair input); the selected output is replayed later with a commit ID.
        var provisional = fusion is not null
            ? !committed && !string.Equals(fusion.Pattern, "single", StringComparison.OrdinalIgnoreCase)
            : _fusionUncommitted;
        if (provisional)
            _provisionalMessages.Add(data.MessageId);
        return [new AssistantDelta(data.Content, provisional)];
    }

    // A Fusion commit replays the selected phase's tool events under the same
    // tool call IDs after they were already published live.
    private static IReadOnlyList<StreamEvent> FirstForTool(
        HashSet<string> seen, string? toolCallId, StreamEvent evt) =>
        string.IsNullOrEmpty(toolCallId) || seen.Add(toolCallId) ? [evt] : [];

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
#pragma warning restore GHCP001
