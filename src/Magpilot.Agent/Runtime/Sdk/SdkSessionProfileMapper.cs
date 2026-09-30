using GitHub.Copilot;

namespace Magpilot.Agent.Runtime.Sdk;

internal static class SdkSessionProfileMapper
{
    public static SessionConfig Create(
        SessionRuntimeProfile profile,
        string workingDirectory,
        string? sessionId = null,
        IList<CustomAgentConfig>? customAgents = null)
    {
        return new SessionConfig
        {
            SessionId = sessionId,
            WorkingDirectory = workingDirectory,
            Model = profile.Model,
            ReasoningEffort = profile.ReasoningEffort,
            Agent = profile.Agent,
            CustomAgents = customAgents,
            Streaming = true,
            AvailableTools = OptionalList(profile.AvailableTools),
            DisabledMcpServers = OptionalList(profile.DisabledMcpServers),
            SkipCustomInstructions = profile.NoCustomInstructions ? true : null,
        };
    }

    public static ResumeSessionConfig Resume(
        SessionRuntimeProfile profile,
        string workingDirectory,
        IList<CustomAgentConfig>? customAgents = null)
    {
        return new ResumeSessionConfig
        {
            WorkingDirectory = workingDirectory,
            Model = profile.Model,
            ReasoningEffort = profile.ReasoningEffort,
            Agent = profile.Agent,
            CustomAgents = customAgents,
            Streaming = true,
            AvailableTools = OptionalList(profile.AvailableTools),
            DisabledMcpServers = OptionalList(profile.DisabledMcpServers),
            SkipCustomInstructions = profile.NoCustomInstructions ? true : null,
        };
    }

    private static IList<string>? OptionalList(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? [.. values] : null;
}
