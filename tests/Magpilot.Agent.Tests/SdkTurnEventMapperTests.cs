using System.Text.Json;
using GitHub.Copilot;
using Magpilot.Agent.Runtime.Sdk;
using Magpilot.Shared.Models;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkTurnEventMapperTests
{
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
