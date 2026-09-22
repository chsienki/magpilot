namespace Magpilot.Agent.Runtime;

public enum SessionRuntimeBackend
{
    Acp,
    Sdk,
}

/// <summary>
/// Complete configuration needed to create, resume, or restore a Copilot
/// session independently of the runtime transport used to host it.
/// </summary>
public sealed record SessionRuntimeProfile(
    bool UseAgency = false,
    string? Model = null,
    string? ReasoningEffort = null,
    IReadOnlyList<string>? DisabledMcpServers = null,
    string? Agent = null,
    IReadOnlyList<string>? AvailableTools = null,
    bool DisableBuiltinMcps = false,
    bool NoCustomInstructions = false,
    string? CopilotHome = null,
    SessionRuntimeBackend Backend = SessionRuntimeBackend.Acp)
{
    public static readonly SessionRuntimeProfile Default = new();

    public static SessionRuntimeProfile Resolve(
        bool useAgency,
        string? model,
        string? reasoningEffort,
        IReadOnlyList<string>? disableMcpServers = null,
        string? agent = null,
        IReadOnlyList<string>? availableTools = null,
        bool disableBuiltinMcps = false,
        bool noCustomInstructions = false,
        string? copilotHome = null,
        SessionRuntimeBackend backend = SessionRuntimeBackend.Acp)
    {
        var requestedModel = string.IsNullOrWhiteSpace(model) ? null : ValidateModel(model);
        var effort = ValidateEffort(reasoningEffort);
        var disabled = ValidateMcpServerNames(disableMcpServers);
        var requestedAgent = string.IsNullOrWhiteSpace(agent) ? null : ValidateAgent(agent);
        var tools = ValidateToolSelectors(availableTools);
        var requestedCopilotHome = string.IsNullOrWhiteSpace(copilotHome)
            ? null
            : ValidateCopilotHome(copilotHome);

        return new SessionRuntimeProfile(
            useAgency,
            requestedModel,
            effort,
            disabled,
            requestedAgent,
            tools,
            disableBuiltinMcps,
            noCustomInstructions,
            requestedCopilotHome,
            backend);
    }

    public static void ValidateAvailableToolsRequest(IReadOnlyList<string>? selectors)
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

    private static IReadOnlyList<string> ValidateMcpServerNames(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return [];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,64}$"))
                throw new ArgumentException($"Invalid MCP server name '{name}'.", nameof(names));
            result.Add(name);
        }
        return result.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

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

    private static string ValidateCopilotHome(string copilotHome)
    {
        if (copilotHome.IndexOf('\0') >= 0 || copilotHome.Length > 1024)
            throw new ArgumentException("Invalid Copilot home path.", nameof(copilotHome));
        if (!Path.IsPathFullyQualified(copilotHome))
            throw new ArgumentException("Copilot home path must be absolute.", nameof(copilotHome));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(copilotHome));
    }
}
