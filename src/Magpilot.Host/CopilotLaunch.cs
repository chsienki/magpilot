namespace Magpilot.Host;

/// <summary>
/// Resolves the real Copilot executable and preserves the user's forwarded
/// arguments verbatim.
/// </summary>
public static class CopilotLaunch
{
    /// <summary>
    /// Resolve the spawn target. <paramref name="copilotArgs"/> is the argv
    /// destined for copilot (already including any <c>--resume=&lt;sid&gt;</c> the
    /// caller injected). Throws <see cref="FileNotFoundException"/> if the
    /// Copilot executable can't be located (callers already handle that).
    /// </summary>
    public static (string Exe, IReadOnlyList<string> Argv) Resolve(
        IReadOnlyList<string> copilotArgs) =>
        (CopilotLocator.Find(), copilotArgs);
}
