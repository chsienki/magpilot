namespace Magpilot.Agent.Runtime.Sdk;

/// <summary>
/// Client-wide runtime isolation settings. Session-scoped model, tools, agent,
/// and working directory do not participate in this key.
/// </summary>
internal sealed record SdkClientKey(string? BaseDirectory)
{
    public static SdkClientKey FromProfile(SessionRuntimeProfile profile)
    {
        var baseDirectory = profile.CopilotHome;
        if (baseDirectory is null)
            return new SdkClientKey((string?)null);

        var normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(baseDirectory));
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        return new SdkClientKey(normalized);
    }
}
