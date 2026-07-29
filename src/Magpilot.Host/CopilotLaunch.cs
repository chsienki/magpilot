namespace Magpilot.Host;

/// <summary>
/// Decides which executable the launcher spawns and with what argv.
///
/// <para>Normally that's the real copilot binary (<see cref="CopilotLocator"/>)
/// with the user's forwarded args verbatim. Under <c>--magpilot-agency</c> it
/// becomes <c>agency copilot &lt;args&gt;</c>: Microsoft's agency CLI wrapping
/// copilot, so the interactive session gets agency's curated MCP servers and
/// tooling. copilot still runs underneath and writes the same
/// <c>~/.copilot/session-state</c> artifacts, so the session shows up in the
/// hub either way.</para>
///
/// <para>The user's forwarded args are handed to agency untouched -- agency's
/// own parser splits them: flags it recognizes (<c>-a</c>/<c>--agent</c>,
/// <c>--profile</c>, <c>--no-default-mcps</c>, ...) are consumed, and the
/// first flag it doesn't recognize (e.g. copilot's <c>--resume</c>) plus
/// everything after it is forwarded to copilot as pass-through. So a user can
/// target either side without the launcher second-guessing them (and can
/// still write an explicit <c>--</c> to force the rest to copilot). Default
/// MCPs stay on -- unlike the agent's ACP flavor, which passes
/// <c>--no-default-mcps</c> -- because the point of an interactive agency
/// session is the curated servers. Top-level agency options (e.g.
/// <c>--verbosity</c>) that must precede the subcommand aren't expressible
/// here; use their env vars (<c>AGENCY_VERBOSITY</c>, ...) instead.</para>
/// </summary>
public static class CopilotLaunch
{
    /// <summary>
    /// Resolve the spawn target. <paramref name="copilotArgs"/> is the argv
    /// destined for copilot (already including any <c>--resume=&lt;sid&gt;</c> the
    /// caller injected). Throws <see cref="FileNotFoundException"/> if the
    /// chosen executable can't be located (callers already handle that).
    /// </summary>
    public static (string Exe, IReadOnlyList<string> Argv) Resolve(bool agency, IReadOnlyList<string> copilotArgs)
    {
        if (!agency)
            return (CopilotLocator.Find(), copilotArgs);

        return (AgencyLocator.Find(), BuildAgencyArgv(copilotArgs));
    }

    /// <summary>
    /// Build the agency argv: <c>copilot &lt;forwardArgs&gt;</c>. The args are
    /// passed through untouched so agency's own parser decides which belong to
    /// agency and which pass through to copilot -- no <c>--</c> is injected, so
    /// agency-specific options remain reachable. Split out from
    /// <see cref="Resolve"/> so the arg shaping is unit-testable without a real
    /// agency binary on PATH.
    /// </summary>
    public static IReadOnlyList<string> BuildAgencyArgv(IReadOnlyList<string> forwardArgs)
    {
        var argv = new List<string>(forwardArgs.Count + 1) { "copilot" };
        argv.AddRange(forwardArgs);
        return argv;
    }
}
