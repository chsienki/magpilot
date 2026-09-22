using Magpilot.Agent.Acp;
using Magpilot.Agent.Runtime;
using Magpilot.Agent.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SessionRuntimeProfileTests
{
    [Fact]
    public void Acp_flavor_preserves_the_protocol_neutral_profile()
    {
        var copilotHome = Path.Combine(Path.GetTempPath(), "isolated-copilot-home");
        var profile = SessionRuntimeProfile.Resolve(
            model: "example-model",
            reasoningEffort: "high",
            disableMcpServers: ["zeta", "alpha"],
            agent: "example-agent",
            availableTools: ["server(tool)", "shell"],
            disableBuiltinMcps: true,
            noCustomInstructions: true,
            copilotHome: copilotHome);

        var roundTripped = AcpFlavor.FromRuntimeProfile(profile).ToRuntimeProfile();

        Assert.Equal(profile.Model, roundTripped.Model);
        Assert.Equal(profile.ReasoningEffort, roundTripped.ReasoningEffort);
        Assert.Equal(profile.DisabledMcpServers, roundTripped.DisabledMcpServers);
        Assert.Equal(profile.Agent, roundTripped.Agent);
        Assert.Equal(profile.AvailableTools, roundTripped.AvailableTools);
        Assert.Equal(profile.DisableBuiltinMcps, roundTripped.DisableBuiltinMcps);
        Assert.Equal(profile.NoCustomInstructions, roundTripped.NoCustomInstructions);
        Assert.Equal(profile.CopilotHome, roundTripped.CopilotHome);
    }

    [Fact]
    public void Acp_manager_implements_the_agent_runtime_contract()
    {
        var manager = new AcpSessionManager(
            new AcpFlavorPool(
                NullLoggerFactory.Instance,
                NullLogger<AcpFlavorPool>.Instance),
            new YoloRegistry(NullLogger<YoloRegistry>.Instance),
            NullLogger<AcpSessionManager>.Instance);

        Assert.IsAssignableFrom<IAgentSessionRuntime>(manager);
    }
}
