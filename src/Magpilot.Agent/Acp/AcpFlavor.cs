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
    IReadOnlyList<string>? DisabledMcpServers = null,
    string? Agent = null,
    IReadOnlyList<string>? AvailableTools = null,
    bool DisableBuiltinMcps = false,
    bool NoCustomInstructions = false,
    string? CopilotHome = null)
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
    /// so they do not change the process key or command line.
    /// </summary>
    public static AcpFlavor ForModel(string model, string? reasoningEffort, IReadOnlyList<string>? disableMcpServers = null)
        => Resolve(useAgency: false, model, reasoningEffort, disableMcpServers);

    /// <summary>
    /// Resolve the process flavor and per-session configuration for a create or
    /// adopt request. Model and reasoning remain session-scoped ACP settings, so
    /// ordinary Copilot sessions share the default multiplexed child even when
    /// they use different models. Agent discovery/selection and every tool,
    /// built-in MCP, instruction, or Copilot-home switch alter process behavior
    /// and therefore produce a distinct child flavor.
    /// </summary>
    public static AcpFlavor Resolve(
        bool useAgency,
        string? model,
        string? reasoningEffort,
        IReadOnlyList<string>? disableMcpServers = null,
        string? agent = null,
        IReadOnlyList<string>? availableTools = null,
        bool disableBuiltinMcps = false,
        bool noCustomInstructions = false,
        string? copilotHome = null)
    {
        var baseFlavor = useAgency ? Agency : Default;
        var requestedModel = string.IsNullOrWhiteSpace(model) ? null : ValidateModel(model);
        var effort = ValidateEffort(reasoningEffort);
        var disabled = ValidateMcpServerNames(disableMcpServers);
        var requestedAgent = string.IsNullOrWhiteSpace(agent) ? null : ValidateAgent(agent);
        var tools = ValidateToolSelectors(availableTools);
        var requestedCopilotHome = string.IsNullOrWhiteSpace(copilotHome)
            ? null
            : ValidateCopilotHome(copilotHome);

        var args = new System.Text.StringBuilder(baseFlavor.Args);
        foreach (var server in disabled)
            args.Append(" --disable-mcp-server ").Append(server);
        if (requestedAgent is not null)
            args.Append(" --agent ").Append(requestedAgent);
        foreach (var tool in tools)
            args.Append(" --available-tools=").Append(tool);
        if (disableBuiltinMcps)
            args.Append(" --disable-builtin-mcps");
        if (noCustomInstructions)
            args.Append(" --no-custom-instructions");

        var hasProcessScope =
            disabled.Count > 0 ||
            requestedAgent is not null ||
            tools.Count > 0 ||
            disableBuiltinMcps ||
            noCustomInstructions ||
            requestedCopilotHome is not null;
        var key = hasProcessScope
            ? $"{baseFlavor.Key}:scope-{ProcessScopeHash(
                disabled,
                requestedAgent,
                tools,
                disableBuiltinMcps,
                noCustomInstructions,
                requestedCopilotHome)}"
            : baseFlavor.Key;

        return baseFlavor with
        {
            Key = key,
            Args = args.ToString(),
            Model = requestedModel,
            ReasoningEffort = effort,
            DisabledMcpServers = disabled,
            Agent = requestedAgent,
            AvailableTools = tools,
            DisableBuiltinMcps = disableBuiltinMcps,
            NoCustomInstructions = noCustomInstructions,
            CopilotHome = requestedCopilotHome,
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

    private static string ValidateAgent(string agent)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(agent, "^[A-Za-z0-9._-]{1,64}$"))
            throw new ArgumentException($"Invalid agent name '{agent}'.", nameof(agent));
        return agent;
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

    // Tool selectors are command-line values such as `magnus-phone` or
    // `server(tool_name)`. Whitespace and quoting characters are deliberately
    // excluded so an HTTP caller cannot turn one selector into extra arguments.
    private static IReadOnlyList<string> ValidateToolSelectors(IReadOnlyList<string>? selectors)
    {
        if (selectors is null || selectors.Count == 0) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var selector in selectors)
        {
            if (string.IsNullOrWhiteSpace(selector)) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(selector, "^[A-Za-z0-9._:/?*()=-]{1,128}$"))
                throw new ArgumentException($"Invalid tool selector '{selector}'.", nameof(selectors));
            result.Add(selector);
        }
        return result.OrderBy(static selector => selector, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void ValidateAvailableToolsRequest(IReadOnlyList<string>? selectors)
    {
        if (selectors is null) return;
        if (selectors.Count == 0 || selectors.All(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Available tools cannot be empty; omit the property to leave tools unrestricted.",
                nameof(selectors));
        }
        if (selectors.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Available tools cannot contain a blank selector.",
                nameof(selectors));
        }
    }

    private static string ValidateCopilotHome(string copilotHome)
    {
        if (copilotHome.IndexOf('\0') >= 0 || copilotHome.Length > 1024)
            throw new ArgumentException("Invalid Copilot home path.", nameof(copilotHome));
        if (!Path.IsPathFullyQualified(copilotHome))
            throw new ArgumentException("Copilot home path must be absolute.", nameof(copilotHome));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(copilotHome));
    }

    private static string CopilotHomeKey(string copilotHome) =>
        OperatingSystem.IsWindows() ? copilotHome.ToUpperInvariant() : copilotHome;

    private static string ProcessScopeHash(
        IReadOnlyList<string> disabledMcpServers,
        string? agent,
        IReadOnlyList<string> availableTools,
        bool disableBuiltinMcps,
        bool noCustomInstructions,
        string? copilotHome)
    {
        var scope = new System.Text.StringBuilder();
        AppendList(scope, disabledMcpServers);
        AppendValue(scope, agent);
        AppendList(scope, availableTools);
        AppendValue(scope, disableBuiltinMcps ? "1" : "0");
        AppendValue(scope, noCustomInstructions ? "1" : "0");
        AppendValue(scope, copilotHome is null ? null : CopilotHomeKey(copilotHome));
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(scope.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AppendList(System.Text.StringBuilder target, IReadOnlyList<string> values)
    {
        target.Append(values.Count).Append(':');
        foreach (var value in values)
            AppendValue(target, value);
    }

    private static void AppendValue(System.Text.StringBuilder target, string? value)
    {
        if (value is null)
        {
            target.Append("-1:");
            return;
        }
        target.Append(value.Length).Append(':').Append(value);
    }
}
