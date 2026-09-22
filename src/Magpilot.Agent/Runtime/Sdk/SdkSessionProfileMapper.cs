using GitHub.Copilot;

namespace Magpilot.Agent.Runtime.Sdk;

internal static class SdkSessionProfileMapper
{
    public static SessionConfig Create(
        SessionRuntimeProfile profile,
        string workingDirectory,
        string? sessionId = null)
    {
        ValidateSupported(profile);
        return new SessionConfig
        {
            SessionId = sessionId,
            WorkingDirectory = workingDirectory,
            Model = profile.Model,
            ReasoningEffort = profile.ReasoningEffort,
            Agent = profile.Agent,
            Streaming = true,
            AvailableTools = OptionalList(profile.AvailableTools),
            DisabledMcpServers = OptionalList(profile.DisabledMcpServers),
            SkipCustomInstructions = profile.NoCustomInstructions ? true : null,
        };
    }

    public static ResumeSessionConfig Resume(
        SessionRuntimeProfile profile,
        string workingDirectory)
    {
        ValidateSupported(profile);
        return new ResumeSessionConfig
        {
            WorkingDirectory = workingDirectory,
            Model = profile.Model,
            ReasoningEffort = profile.ReasoningEffort,
            Agent = profile.Agent,
            Streaming = true,
            AvailableTools = OptionalList(profile.AvailableTools),
            DisabledMcpServers = OptionalList(profile.DisabledMcpServers),
            SkipCustomInstructions = profile.NoCustomInstructions ? true : null,
        };
    }

    private static void ValidateSupported(SessionRuntimeProfile profile)
    {
        if (profile.UseAgency)
        {
            throw new NotSupportedException(
                "Agency sessions remain on the ACP backend until Agency exposes a compatible SDK runtime.");
        }

        if (profile.DisableBuiltinMcps)
        {
            throw new NotSupportedException(
                "The SDK backend cannot yet prove equivalence for disabling every built-in MCP server.");
        }
    }

    private static IList<string>? OptionalList(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } ? [.. values] : null;
}
