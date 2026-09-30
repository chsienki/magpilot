using Magpilot.Agent.Runtime;
using Magpilot.Agent.Runtime.Sdk;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkCustomAgentLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"magpilot-agent-{Guid.NewGuid():N}");

    [Fact]
    public void Load_maps_selected_agent_frontmatter_and_prompt()
    {
        var home = Path.Combine(_root, "home");
        WriteAgent(
            Path.Combine(home, "agents"),
            "phone",
            """
            ---
            name: Phone Router
            description: Fast spoken router.
            tools:
              - phone/*
              - reminders/*
            model: example-model
            reasoning-effort: none
            infer: false
            ---
            Route each request safely.
            """);
        var profile = SessionRuntimeProfile.Resolve(
            model: null,
            reasoningEffort: null,
            agent: "phone",
            copilotHome: home);

        var customAgents = SdkCustomAgentLoader.Load(
            profile,
            Path.Combine(_root, "work"));

        var agent = Assert.Single(customAgents!);
        Assert.Equal("phone", agent.Name);
        Assert.Equal("Phone Router", agent.DisplayName);
        Assert.Equal("Fast spoken router.", agent.Description);
        Assert.Equal(
            ["phone/*", "reminders/*"],
            agent.Tools);
        Assert.Equal("example-model", agent.Model);
        Assert.Equal("none", agent.ReasoningEffort);
        Assert.False(agent.Infer);
        Assert.Equal("Route each request safely.", agent.Prompt);
    }

    [Fact]
    public void Load_falls_back_to_project_agent_without_custom_home()
    {
        var workingDirectory = Path.Combine(_root, "work");
        WriteAgent(
            Path.Combine(workingDirectory, ".github", "agents"),
            "project-router",
            """
            ---
            description: Project router.
            tools: [shell, view]
            ---
            Handle project requests.
            """);
        var profile = SessionRuntimeProfile.Resolve(
            model: null,
            reasoningEffort: null,
            agent: "project-router");

        var customAgents = SdkCustomAgentLoader.Load(
            profile,
            workingDirectory);

        var agent = Assert.Single(customAgents!);
        Assert.Equal(["shell", "view"], agent.Tools);
        Assert.Equal("Handle project requests.", agent.Prompt);
    }

    [Fact]
    public void Load_returns_null_without_selected_agent()
    {
        Assert.Null(SdkCustomAgentLoader.Load(
            SessionRuntimeProfile.Default,
            _root));
    }

    [Fact]
    public void Load_rejects_missing_selected_agent()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(home, "agents"));
        var profile = SessionRuntimeProfile.Resolve(
            model: null,
            reasoningEffort: null,
            agent: "missing",
            copilotHome: home);

        var exception = Assert.Throws<ArgumentException>(
            () => SdkCustomAgentLoader.Load(profile, _root));

        Assert.Contains("Custom agent 'missing' was not found",
            exception.Message);
    }

    [Fact]
    public void Load_rejects_agent_without_prompt_body()
    {
        var home = Path.Combine(_root, "home");
        WriteAgent(
            Path.Combine(home, "agents"),
            "empty",
            """
            ---
            description: Empty agent.
            ---
            """);
        var profile = SessionRuntimeProfile.Resolve(
            model: null,
            reasoningEffort: null,
            agent: "empty",
            copilotHome: home);

        var exception = Assert.Throws<ArgumentException>(
            () => SdkCustomAgentLoader.Load(profile, _root));

        Assert.Contains("prompt body is required", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void WriteAgent(
        string directory,
        string name,
        string contents)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, $"{name}.agent.md"),
            contents);
    }
}
