namespace Magpilot.Agent.Acp;

/// <summary>
/// Describes how to spawn an ACP child process. Every distinct (Exe, Args)
/// pair gets its own long-lived process inside <see cref="AcpFlavorPool"/>
/// when <see cref="MultiplexesSessions"/> is true; otherwise a fresh process
/// is spawned per session.
///
/// "Default" wraps nothing -- just <c>copilot --acp --allow-all-tools</c>.
/// One process multiplexes any number of ACP sessions.
///
/// "Agency" wraps via Microsoft's `agency` CLI, which adds Microsoft-internal
/// MCP servers above plain Copilot CLI. Empirically the agency-wrapped child
/// does NOT multiplex sessions cleanly (a second session/new on the same
/// child hangs), so we spawn a dedicated child per agency session.
///
/// Sessions are tagged with the flavor key they were created against so the
/// session manager can route prompt/stream/cancel calls to the right child.
/// </summary>
public sealed record AcpFlavor(string Key, string Exe, string Args, bool MultiplexesSessions = true)
{
    /// <summary>
    /// The default Copilot CLI flavor. One instance is started eagerly at
    /// agent boot; all sessions created without an explicit flavor use this.
    /// </summary>
    public static readonly AcpFlavor Default =
        new("default",
            OperatingSystem.IsWindows() ? "copilot.exe" : "copilot",
            "--acp --allow-all-tools",
            MultiplexesSessions: true);

    /// <summary>
    /// Agency-wrapped Copilot. <c>agency copilot</c> adds a curated set of
    /// Microsoft-internal MCP servers and other tooling above the regular
    /// Copilot CLI experience. Each agency session gets its own child
    /// process because agency's session-multiplexing isn't reliable.
    ///
    /// Per-session MCP customization (which MCPs to add explicitly) is a
    /// future enhancement.
    /// </summary>
    public static readonly AcpFlavor Agency =
        new("agency",
            OperatingSystem.IsWindows() ? "agency.exe" : "agency",
            "copilot --no-default-mcps -- --acp --allow-all-tools",
            MultiplexesSessions: false);
}
