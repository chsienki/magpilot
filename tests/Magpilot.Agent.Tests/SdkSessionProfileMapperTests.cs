using GitHub.Copilot;
using Magpilot.Agent.Runtime;
using Magpilot.Agent.Runtime.Sdk;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkSessionProfileMapperTests
{
    [Theory]
    [InlineData("standard", "balance")]
    [InlineData("high", "hydrafusion")]
    [InlineData("max", "hydrafusion-max")]
    public void Fusion_configuration_preserves_routing_without_pinning_reasoning(
        string tier,
        string routingPreference)
    {
        var profile = SessionRuntimeProfile.Resolve("auto", null, fusionTier: tier);
        var config = SdkSessionProfileMapper.Resume(profile, "C:\\work");

        Assert.Equal("auto", config.Model);
        Assert.Null(config.ReasoningEffort);
        Assert.Equal(routingPreference, SdkSessionProfileMapper.AutoTierFor(profile.FusionTier)!.Value.Value);
    }

    [Theory]
    [InlineData("model-1", null, "high")]
    [InlineData("auto", "high", "high")]
    [InlineData("auto", null, "invalid")]
    public void Fusion_rejects_incompatible_configuration(
        string model,
        string? reasoning,
        string tier) =>
        Assert.Throws<ArgumentException>(() =>
            SessionRuntimeProfile.Resolve(model, reasoning, fusionTier: tier));

    [Fact]
    public void Create_maps_supported_session_configuration()
    {
        var profile = SessionRuntimeProfile.Resolve(
            model: "example-model",
            reasoningEffort: "high",
            disableMcpServers: ["example-mcp"],
            agent: "example-agent",
            availableTools: ["shell", "example(tool)"],
            noCustomInstructions: true);

        var config = SdkSessionProfileMapper.Create(
            profile,
            workingDirectory: "C:\\work",
            sessionId: "session-1",
            customAgents:
            [
                new CustomAgentConfig
                {
                    Name = "example-agent",
                    Description = "Example",
                    Prompt = "Act as an example.",
                },
            ]);

        Assert.Equal("session-1", config.SessionId);
        Assert.Equal("C:\\work", config.WorkingDirectory);
        Assert.Equal("example-model", config.Model);
        Assert.Equal("high", config.ReasoningEffort);
        Assert.Equal("example-agent", config.Agent);
        Assert.Equal(
            "example-agent",
            Assert.Single(config.CustomAgents!).Name);
        Assert.True(config.Streaming);
        Assert.Equal(["example(tool)", "shell"], config.AvailableTools);
        Assert.Equal(["example-mcp"], config.DisabledMcpServers);
        Assert.True(config.SkipCustomInstructions);
    }

    [Fact]
    public void Create_omits_empty_tool_and_mcp_lists()
    {
        var config = SdkSessionProfileMapper.Create(
            SessionRuntimeProfile.Default,
            workingDirectory: "C:\\work");

        Assert.Null(config.AvailableTools);
        Assert.Null(config.DisabledMcpServers);
        Assert.Null(config.SkipCustomInstructions);
        Assert.True(config.Streaming);
    }

    [Fact]
    public void Resume_restores_the_complete_supported_profile()
    {
        var profile = SessionRuntimeProfile.Resolve(
            model: "example-model",
            reasoningEffort: "low",
            agent: "example-agent");

        var config = SdkSessionProfileMapper.Resume(
            profile,
            "C:\\work",
            [
                new CustomAgentConfig
                {
                    Name = "example-agent",
                    Description = "Example",
                    Prompt = "Act as an example.",
                },
            ]);

        Assert.Equal("C:\\work", config.WorkingDirectory);
        Assert.Equal("example-model", config.Model);
        Assert.Equal("low", config.ReasoningEffort);
        Assert.Equal("example-agent", config.Agent);
        Assert.Equal(
            "example-agent",
            Assert.Single(config.CustomAgents!).Name);
    }

    [Fact]
    public void Create_maps_explicit_builtin_github_mcp_exclusion()
    {
        var profile = SessionRuntimeProfile.Resolve(
            model: null,
            reasoningEffort: null,
            disableMcpServers: ["github-mcp-server"]);

        var config = SdkSessionProfileMapper.Create(profile, "C:\\work");

        Assert.Equal(["github-mcp-server"], config.DisabledMcpServers);
    }
}
