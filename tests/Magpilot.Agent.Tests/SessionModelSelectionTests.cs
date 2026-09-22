using Magpilot.Agent.Runtime;
using Magpilot.Shared.Models;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SessionModelSelectionTests
{
    private static readonly SessionModelOption Model = new(
        "model-1",
        "Model One",
        ["low", "medium", "high"],
        "medium");

    [Fact]
    public void Preserves_current_reasoning_when_supported() =>
        Assert.Equal(
            "high",
            SessionModelSelection.ResolveReasoningEffort(
                requested: null,
                current: "high",
                Model));

    [Fact]
    public void Uses_model_default_when_current_reasoning_is_unsupported() =>
        Assert.Equal(
            "medium",
            SessionModelSelection.ResolveReasoningEffort(
                requested: null,
                current: "xhigh",
                Model));

    [Fact]
    public void Rejects_an_explicit_unsupported_reasoning_effort()
    {
        var error = Assert.Throws<SessionModelUpdateException>(() =>
            SessionModelSelection.ResolveReasoningEffort(
                requested: "xhigh",
                current: "high",
                Model));

        Assert.Contains("does not support", error.Message);
    }
}
