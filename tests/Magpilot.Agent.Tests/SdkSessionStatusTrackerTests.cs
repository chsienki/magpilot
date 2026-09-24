#pragma warning disable GHCP001

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Magpilot.Agent.Runtime;
using Magpilot.Agent.Runtime.Sdk;
using Magpilot.Shared.Models;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkSessionStatusTrackerTests
{
    [Fact]
    public void Tracker_combines_model_context_and_session_credits()
    {
        var tracker = new SdkSessionStatusTracker(
            SessionRuntimeProfile.Resolve(
                model: null,
                reasoningEffort: null));
        tracker.SetModels(
        [
            new ModelInfo
            {
                Id = "model-1",
                Name = "Model One",
                SupportedReasoningEfforts = ["low", "high"],
                DefaultReasoningEffort = "low",
            },
        ]);
        tracker.Apply(new CurrentModel
        {
            ModelId = "model-1",
            ReasoningEffort = "high",
        });
        tracker.Apply(new SessionUsageInfoEvent
        {
            Data = new SessionUsageInfoData
            {
                CurrentTokens = 25,
                TokenLimit = 100,
                MessagesLength = 3,
            },
        });
        tracker.Apply(new UsageGetMetricsResult
        {
            TotalNanoAiu = 1_500_000_000,
        });

        var status = tracker.Snapshot;
        Assert.Equal("model-1", status.ModelId);
        Assert.Equal("Model One", status.ModelName);
        Assert.Equal("high", status.ReasoningEffort);
        Assert.Equal(25, status.CurrentTokens);
        Assert.Equal(100, status.TokenLimit);
        Assert.Equal(1.5, status.AiCreditsUsed);
        Assert.True(status.CanEditModel);
        Assert.Equal("model-1", tracker.Profile.Model);
        Assert.Equal("high", tracker.Profile.ReasoningEffort);
    }

    [Fact]
    public void Live_usage_is_reconciled_by_the_authoritative_checkpoint()
    {
        var tracker = new SdkSessionStatusTracker(
            SessionRuntimeProfile.Default);

        tracker.Apply(new AssistantUsageEvent
        {
            Data = new AssistantUsageData
            {
                Model = "model-1",
                CopilotUsage = new AssistantUsageCopilotUsage
                {
                    TotalNanoAiu = 250_000_000,
                },
            },
        });
        Assert.Equal(0.25, tracker.Snapshot.AiCreditsUsed);

        tracker.Apply(new SessionUsageCheckpointEvent
        {
            Data = new SessionUsageCheckpointData
            {
                TotalNanoAiu = 500_000_000,
            },
        });
        Assert.Equal(0.5, tracker.Snapshot.AiCreditsUsed);
    }

    [Fact]
    public void Model_options_preserve_runtime_metadata()
    {
        var tracker = new SdkSessionStatusTracker(
            SessionRuntimeProfile.Default);
        tracker.SetModels(
        [
            new ModelInfo
            {
                Id = "z-model",
                Name = "Zulu",
                SupportedReasoningEfforts = ["none"],
                DefaultReasoningEffort = "none",
            },
            new ModelInfo
            {
                Id = "a-model",
                Name = "Alpha",
                SupportedReasoningEfforts = ["low", "medium"],
                DefaultReasoningEffort = "medium",
            },
        ]);

        Assert.Collection(
            tracker.ModelOptions,
            option =>
            {
                Assert.Equal("a-model", option.Id);
                Assert.Equal(["low", "medium"], option.SupportedReasoningEfforts);
                Assert.Equal("medium", option.DefaultReasoningEffort);
            },
            option => Assert.Equal("z-model", option.Id));
    }

    [Theory]
    [InlineData(1_000_000_000, 1)]
    [InlineData(125_000_000, 0.125)]
    public void Nano_ai_units_convert_to_ai_credits(
        double nanoAiUnits,
        double expected) =>
        Assert.Equal(expected, SdkSessionStatusTracker.ToAiCredits(nanoAiUnits));
}

#pragma warning restore GHCP001
