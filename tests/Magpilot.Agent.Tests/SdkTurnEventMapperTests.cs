#pragma warning disable GHCP001
using System.Text.Json;
using GitHub.Copilot;
using Magpilot.Agent.Runtime.Sdk;
using Magpilot.Shared.Models;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkTurnEventMapperTests
{
    [Fact]
    public void Committed_output_without_deltas_is_delivered_once()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        var message = new AssistantMessageEvent
        {
            Data = new AssistantMessageData
            {
                MessageId = "fusion-final",
                Content = "Final answer",
            },
        };

        Assert.Equal(new AssistantDelta("Final answer"), Assert.Single(mapper.Map(message)));
        Assert.Empty(mapper.Map(message));
    }

    [Fact]
    public void Final_message_does_not_duplicate_streamed_output()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        mapper.Map(new AssistantMessageDeltaEvent
        {
            Data = new AssistantMessageDeltaData
            {
                MessageId = "streamed",
                DeltaContent = "Answer",
            },
        });

        Assert.Empty(mapper.Map(new AssistantMessageEvent
        {
            Data = new AssistantMessageData
            {
                MessageId = "streamed",
                Content = "Answer",
            },
        }));
    }

    [Fact]
    public void Fusion_candidate_is_provisional_and_commit_is_final()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        mapper.Map(DraftPhaseStarted());

        var draft = Assert.Single(mapper.Map(FusionMessage("draft", "Draft", commitId: null)));
        var review = mapper.Map(FusionMessage("critic", "Revise it", commitId: null, scope: "review"));
        var final = Assert.Single(mapper.Map(FusionMessage("final", "Final", commitId: "commit-1")));

        Assert.Equal(new AssistantDelta("Draft", Provisional: true), draft);
        Assert.Empty(review);
        Assert.Equal(new AssistantDelta("Final"), final);
    }

    [Fact]
    public void Deltas_during_an_uncommitted_fusion_phase_are_provisional()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        mapper.Map(DraftPhaseStarted());

        var delta = Assert.Single(mapper.Map(new AssistantMessageDeltaEvent
        {
            Data = new AssistantMessageDeltaData { MessageId = "draft", DeltaContent = "Dr" },
        }));
        mapper.Map(FusionMessage("final", "Final", commitId: "commit-1"));
        var after = Assert.Single(mapper.Map(new AssistantMessageDeltaEvent
        {
            Data = new AssistantMessageDeltaData { MessageId = "next", DeltaContent = "More" },
        }));

        Assert.Equal(new AssistantDelta("Dr", Provisional: true), delta);
        Assert.Equal(new AssistantDelta("More"), after);
    }

    [Fact]
    public void Replayed_fusion_tool_events_are_published_once()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        var start = new ToolExecutionStartEvent
        {
            Data = new ToolExecutionStartData { ToolCallId = "tool-1", ToolName = "view" },
        };
        var complete = new ToolExecutionCompleteEvent
        {
            Data = new ToolExecutionCompleteData { ToolCallId = "tool-1", Success = true },
        };

        Assert.Single(mapper.Map(start));
        Assert.Single(mapper.Map(complete));
        Assert.Empty(mapper.Map(start));
        Assert.Empty(mapper.Map(complete));
    }

    [Fact]
    public void Commit_reusing_a_candidate_message_id_is_delivered_as_final()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        mapper.Map(DraftPhaseStarted());

        var draft = Assert.Single(mapper.Map(FusionMessage("same", "Answer", commitId: null)));
        var final = Assert.Single(mapper.Map(FusionMessage("same", "Answer", commitId: "commit-1")));

        Assert.True(((AssistantDelta)draft).Provisional);
        Assert.Equal(new AssistantDelta("Answer"), final);
    }

    [Fact]
    public void Single_pattern_output_is_never_provisional()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();

        var live = Assert.Single(mapper.Map(FusionMessage("same", "Answer", commitId: null, pattern: "single")));
        var replay = mapper.Map(FusionMessage("same", "Answer", commitId: "commit-1", pattern: "single"));

        Assert.Equal(new AssistantDelta("Answer"), live);
        Assert.Empty(replay);
    }

    [Fact]
    public void Later_candidate_phases_are_withheld_until_the_commit()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        mapper.Map(DraftPhaseStarted());
        var draft = Assert.Single(mapper.Map(FusionMessage("d", "Draft", commitId: null, phaseId: "draft")));
        mapper.Map(DraftPhaseStarted("critic", FusionPhaseKind.Critic, FusionConversationScope.Review));
        mapper.Map(DraftPhaseStarted("revision", FusionPhaseKind.Revision));

        var revision = mapper.Map(FusionMessage("r", "Revised", commitId: null, phaseId: "revision"));
        var final = Assert.Single(mapper.Map(FusionMessage("c", "Revised", commitId: "commit-1", phaseId: "revision")));

        Assert.Equal(new AssistantDelta("Draft", Provisional: true), draft);
        Assert.Empty(revision);
        Assert.Equal(new AssistantDelta("Revised"), final);
    }

    private static AssistantFusionPhaseStartedEvent DraftPhaseStarted(
        string phaseId = "draft",
        FusionPhaseKind? kind = null,
        FusionConversationScope? scope = null) => new()
    {
        Data = new AssistantFusionPhaseStartedData
        {
            FusionId = "fusion-1",
            PhaseId = phaseId,
            PhaseKind = kind ?? FusionPhaseKind.Draft,
            Pattern = FusionPattern.Critique,
            ConversationScope = scope ?? FusionConversationScope.Root,
            Model = "model",
            Role = phaseId,
        },
    };

    private static AssistantMessageEvent FusionMessage(
        string messageId, string content, string? commitId, string scope = "root",
        string pattern = "critique", string? phaseId = null) => new()
    {
        Data = new AssistantMessageData
        {
            MessageId = messageId,
            Content = content,
            Fusion = new FusionAttribution
            {
                FusionId = "fusion-1",
                Pattern = pattern,
                PhaseId = phaseId,
                Policy = "high",
                SyntheticModel = "auto",
                CommitId = commitId,
                ConversationScope = scope,
            },
        },
    };

    [Fact]
    public void Streaming_message_and_reasoning_deltas_map_to_existing_events()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();

        var message = Assert.Single(mapper.Map(new AssistantMessageDeltaEvent
        {
            Data = new AssistantMessageDeltaData
            {
                MessageId = "message-1",
                DeltaContent = "hello",
            },
        }));
        var reasoning = Assert.Single(mapper.Map(new AssistantReasoningDeltaEvent
        {
            Data = new AssistantReasoningDeltaData
            {
                ReasoningId = "reasoning-1",
                DeltaContent = "thinking",
            },
        }));

        Assert.Equal(new AssistantDelta("hello"), message);
        Assert.Equal(new ThoughtDelta("thinking"), reasoning);
    }

    [Fact]
    public void Tool_lifecycle_maps_start_progress_and_success()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();
        using var arguments = JsonDocument.Parse("""{"path":"README.md"}""");

        var started = Assert.IsType<ToolCallStart>(Assert.Single(mapper.Map(
            new ToolExecutionStartEvent
            {
                Data = new ToolExecutionStartData
                {
                    ToolCallId = "tool-1",
                    ToolName = "view",
                    Arguments = arguments.RootElement.Clone(),
                },
            })));
        var progress = Assert.IsType<ToolCallProgress>(Assert.Single(mapper.Map(
            new ToolExecutionProgressEvent
            {
                Data = new ToolExecutionProgressData
                {
                    ToolCallId = "tool-1",
                    ProgressMessage = "reading",
                },
            })));
        var completed = Assert.IsType<ToolCallEnd>(Assert.Single(mapper.Map(
            new ToolExecutionCompleteEvent
            {
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = "tool-1",
                    Success = true,
                    Result = new ToolExecutionCompleteResult
                    {
                        Content = "done",
                    },
                },
            })));

        Assert.Equal("view", started.Name);
        Assert.Equal("""{"path":"README.md"}""", started.RawInput);
        Assert.Equal("reading", progress.PartialOutput);
        Assert.True(completed.Success);
        Assert.Equal("done", completed.Result);
    }

    [Fact]
    public void Tool_failure_uses_the_sdk_error_message()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();

        var completed = Assert.IsType<ToolCallEnd>(Assert.Single(mapper.Map(
            new ToolExecutionCompleteEvent
            {
                Data = new ToolExecutionCompleteData
                {
                    ToolCallId = "tool-1",
                    Success = false,
                    Error = new ToolExecutionCompleteError
                    {
                        Message = "failed",
                    },
                },
            })));

        Assert.False(completed.Success);
        Assert.Equal("failed", completed.Result);
    }

    [Fact]
    public void Session_error_publishes_one_terminal_boundary()
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();

        var errorEvents = mapper.Map(new SessionErrorEvent
        {
            Data = new SessionErrorData
            {
                ErrorType = "provider",
                Message = "provider failed",
            },
        });
        var idleEvents = mapper.Map(new SessionIdleEvent
        {
            Data = new SessionIdleData(),
        });

        Assert.Collection(
            errorEvents,
            evt => Assert.Equal(new ErrorEvent("provider failed"), evt),
            evt => Assert.Equal(new TurnComplete("error"), evt));
        Assert.Empty(idleEvents);
    }

    [Theory]
    [InlineData(false, "end_turn")]
    [InlineData(true, "cancelled")]
    public void Session_idle_is_the_clean_turn_boundary(
        bool aborted,
        string expectedReason)
    {
        var mapper = new SdkTurnEventMapper();
        mapper.BeginTurn();

        var completed = Assert.Single(mapper.Map(new SessionIdleEvent
        {
            Data = new SessionIdleData { Aborted = aborted },
        }));

        Assert.Equal(new TurnComplete(expectedReason), completed);
    }

    [Fact]
    public void Events_outside_an_active_turn_are_ignored()
    {
        var mapper = new SdkTurnEventMapper();

        var events = mapper.Map(new AssistantMessageDeltaEvent
        {
            Data = new AssistantMessageDeltaData
            {
                MessageId = "message-1",
                DeltaContent = "startup",
            },
        });

        Assert.Empty(events);
    }
}
#pragma warning restore GHCP001
