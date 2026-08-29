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
public sealed record AcpFlavor(
    string Key,
    string Exe,
    string Args,
    bool MultiplexesSessions = true,
    string? Model = null,
    string? ReasoningEffort = null,
    IReadOnlyList<string>? DisabledMcpServers = null)
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
    /// Build the default Copilot process flavor plus per-session model/reasoning
    /// configuration. Model and reasoning are applied through ACP config options,
    /// so they do not change the process key or command line. Only process-scoped
    /// settings such as disabled MCP servers create an isolated child.
    /// </summary>
    public static AcpFlavor ForModel(string model, string? reasoningEffort, IReadOnlyList<string>? disableMcpServers = null)
        => Resolve(useAgency: false, model, reasoningEffort, disableMcpServers);

    /// <summary>
    /// Resolve the process flavor and per-session configuration for a create or
    /// adopt request. Model and reasoning remain session-scoped ACP settings, so
    /// ordinary Copilot sessions share the default multiplexed child even when
    /// they use different models. Disabled MCP servers alter the process tool
    /// surface and therefore produce a distinct child flavor.
    /// </summary>
    public static AcpFlavor Resolve(bool useAgency, string? model, string? reasoningEffort, IReadOnlyList<string>? disableMcpServers = null)
    {
        var baseFlavor = useAgency ? Agency : Default;
        var requestedModel = string.IsNullOrWhiteSpace(model) ? null : ValidateModel(model);
        var effort = ValidateEffort(reasoningEffort);
        var disabled = ValidateMcpServerNames(disableMcpServers);

        var args = new System.Text.StringBuilder(baseFlavor.Args);
        foreach (var server in disabled)
            args.Append(" --disable-mcp-server ").Append(server);

        var key = baseFlavor.Key;
        if (disabled.Count > 0)
            key += ":no-" + string.Join("+", disabled);

        return baseFlavor with
        {
            Key = key,
            Args = args.ToString(),
            Model = requestedModel,
            ReasoningEffort = effort,
            DisabledMcpServers = disabled,
        };
    }

    // Model + effort arrive over HTTP and are interpolated into the child's
    // ACP request, while MCP names are also interpolated into the child command
    // line. Keep validation generic and constrain all of them to safe tokens;
    // the session's advertised configOptions are authoritative for whether a
    // particular model or reasoning value is supported.
    private static string ValidateModel(string model)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(model, "^[A-Za-z0-9._-]{1,64}$"))
            throw new ArgumentException($"Invalid model id '{model}'.", nameof(model));
        return model;
    }

    private static string? ValidateEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort)) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(effort, "^[A-Za-z0-9._-]{1,64}$"))
            throw new ArgumentException($"Invalid reasoning effort '{effort}'.", nameof(effort));
        return effort.ToLowerInvariant();
    }

    // MCP server names arrive over HTTP and are interpolated into the child's
    // command line as `--disable-mcp-server <name>`, so constrain them to safe
    // tokens -- same guard as the model id -- to stop them injecting extra args.
    private static IReadOnlyList<string> ValidateMcpServerNames(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(n, "^[A-Za-z0-9._-]{1,64}$"))
                throw new ArgumentException($"Invalid MCP server name '{n}'.", nameof(names));
            result.Add(n);
        }
        return result.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
