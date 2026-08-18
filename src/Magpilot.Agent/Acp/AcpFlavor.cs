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

    /// <summary>
    /// A model-pinned Copilot flavor: <c>copilot --acp --allow-all-tools --model
    /// &lt;model&gt; [--reasoning-effort &lt;effort&gt;]</c>. Lets a caller run a session on a
    /// specific (e.g. faster, cheaper) model with a chosen reasoning effort,
    /// isolated from the default child that multiplexes everything else. The key
    /// embeds model+effort so the pool caches one child per distinct combination;
    /// sessions requesting the same model+effort share it.
    /// </summary>
    public static AcpFlavor ForModel(string model, string? reasoningEffort)
    {
        var m = ValidateModel(model);
        var effort = ValidateEffort(reasoningEffort);
        var exe = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
        var args = effort is null
            ? $"--acp --allow-all-tools --model {m}"
            : $"--acp --allow-all-tools --model {m} --reasoning-effort {effort}";
        var key = effort is null ? $"model:{m}" : $"model:{m}:{effort}";
        return new AcpFlavor(key, exe, args, MultiplexesSessions: true);
    }

    /// <summary>
    /// Resolve the flavor for a session create/adopt request. A
    /// <paramref name="model"/> wins (pinned model flavor); otherwise
    /// <paramref name="useAgency"/> selects the agency wrapper; otherwise the
    /// default multiplexed Copilot.
    /// </summary>
    public static AcpFlavor Resolve(bool useAgency, string? model, string? reasoningEffort) =>
        !string.IsNullOrWhiteSpace(model) ? ForModel(model, reasoningEffort)
        : useAgency ? Agency
        : Default;

    private static readonly HashSet<string> ValidEfforts =
        new(StringComparer.OrdinalIgnoreCase) { "none", "minimal", "low", "medium", "high", "xhigh", "max" };

    // Model + effort arrive over HTTP and are interpolated into the child's
    // command line, so constrain them to safe tokens (no spaces, quotes, or shell
    // metacharacters) to stop them injecting extra arguments into the spawn.
    private static string ValidateModel(string model)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(model, "^[A-Za-z0-9._-]{1,64}$"))
            throw new ArgumentException($"Invalid model id '{model}'.", nameof(model));
        return model;
    }

    private static string? ValidateEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return null;
        if (!ValidEfforts.Contains(effort))
            throw new ArgumentException($"Invalid reasoning effort '{effort}'.", nameof(effort));
        return effort.ToLowerInvariant();
    }
}
