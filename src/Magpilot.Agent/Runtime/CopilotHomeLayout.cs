namespace Magpilot.Agent.Runtime;

internal static class CopilotHomeLayout
{
    public static void Validate(SessionRuntimeProfile profile)
    {
        if (profile.CopilotHome is null)
            return;

        if (!Directory.Exists(profile.CopilotHome))
        {
            throw new ArgumentException(
                $"Copilot home '{profile.CopilotHome}' does not exist or is not ready.",
                nameof(SessionRuntimeProfile.CopilotHome));
        }

        var sessionStatePath = Path.Combine(
            profile.CopilotHome,
            "session-state");
        var sessionState = new DirectoryInfo(sessionStatePath);
        var target = sessionState.Exists
            ? sessionState.ResolveLinkTarget(returnFinalTarget: true)
            : null;
        var scannerRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "session-state"));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (target is null ||
            !string.Equals(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(target.FullName)),
                Path.TrimEndingDirectorySeparator(scannerRoot),
                comparison))
        {
            throw new ArgumentException(
                $"Copilot home '{profile.CopilotHome}' must link session-state to '{scannerRoot}'.",
                nameof(SessionRuntimeProfile.CopilotHome));
        }

        if (profile.Agent is not null &&
            !Directory.Exists(Path.Combine(profile.CopilotHome, "agents")))
        {
            throw new ArgumentException(
                $"Copilot home '{profile.CopilotHome}' has no agents directory for '{profile.Agent}'.",
                nameof(SessionRuntimeProfile.CopilotHome));
        }
    }
}
