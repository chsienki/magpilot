using Magpilot.Agent.Runtime;

namespace Magpilot.Agent.Acp;

/// <summary>
/// Describes how to spawn an ACP child process. Every distinct (Exe, Args)
/// pair gets its own long-lived process inside <see cref="AcpFlavorPool"/>.
/// </summary>
public sealed record AcpFlavor(
    string Key,
    string Exe,
    string Args,
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
    /// Plain Copilot CLI. One eager child multiplexes ordinary sessions.
    /// </summary>
    public static readonly AcpFlavor Default =
        new("default",
            OperatingSystem.IsWindows() ? "copilot.exe" : "copilot",
            "--acp --allow-all-tools");

    /// <summary>
    /// Builds the default process flavor plus session-scoped model and
    /// reasoning configuration.
    /// </summary>
    public static AcpFlavor ForModel(
        string model,
        string? reasoningEffort,
        IReadOnlyList<string>? disableMcpServers = null) =>
        Resolve(
            model,
            reasoningEffort,
            disableMcpServers);

    /// <summary>
    /// Retained compatibility factory for ACP-focused tests and internals.
    /// Runtime consumers resolve <see cref="SessionRuntimeProfile"/> instead.
    /// </summary>
    public static AcpFlavor Resolve(
        string? model,
        string? reasoningEffort,
        IReadOnlyList<string>? disableMcpServers = null,
        string? agent = null,
        IReadOnlyList<string>? availableTools = null,
        bool disableBuiltinMcps = false,
        bool noCustomInstructions = false,
        string? copilotHome = null) =>
        FromRuntimeProfile(SessionRuntimeProfile.Resolve(
            model,
            reasoningEffort,
            disableMcpServers,
            agent,
            availableTools,
            disableBuiltinMcps,
            noCustomInstructions,
            copilotHome));

    /// <summary>
    /// Maps protocol-neutral session configuration to ACP process arguments
    /// and a stable process-scope key.
    /// </summary>
    internal static AcpFlavor FromRuntimeProfile(SessionRuntimeProfile profile)
    {
        var baseFlavor = Default;
        var disabled = profile.DisabledMcpServers ?? [];
        var tools = profile.AvailableTools ?? [];

        var args = new System.Text.StringBuilder(baseFlavor.Args);
        foreach (var server in disabled)
            args.Append(" --disable-mcp-server ").Append(server);
        if (profile.Agent is not null)
            args.Append(" --agent ").Append(profile.Agent);
        foreach (var tool in tools)
            args.Append(" --available-tools=").Append(tool);
        if (profile.DisableBuiltinMcps)
            args.Append(" --disable-builtin-mcps");
        if (profile.NoCustomInstructions)
            args.Append(" --no-custom-instructions");

        var hasProcessScope =
            disabled.Count > 0 ||
            profile.Agent is not null ||
            tools.Count > 0 ||
            profile.DisableBuiltinMcps ||
            profile.NoCustomInstructions ||
            profile.CopilotHome is not null;
        var key = hasProcessScope
            ? $"{baseFlavor.Key}:scope-{ProcessScopeHash(
                disabled,
                profile.Agent,
                tools,
                profile.DisableBuiltinMcps,
                profile.NoCustomInstructions,
                profile.CopilotHome)}"
            : baseFlavor.Key;

        return baseFlavor with
        {
            Key = key,
            Args = args.ToString(),
            Model = profile.Model,
            ReasoningEffort = profile.ReasoningEffort,
            DisabledMcpServers = disabled,
            Agent = profile.Agent,
            AvailableTools = tools,
            DisableBuiltinMcps = profile.DisableBuiltinMcps,
            NoCustomInstructions = profile.NoCustomInstructions,
            CopilotHome = profile.CopilotHome,
        };
    }

    internal static void ValidateAvailableToolsRequest(
        IReadOnlyList<string>? selectors) =>
        SessionRuntimeProfile.ValidateAvailableToolsRequest(selectors);

    internal SessionRuntimeProfile ToRuntimeProfile() =>
        new(
            Model,
            ReasoningEffort,
            DisabledMcpServers,
            Agent,
            AvailableTools,
            DisableBuiltinMcps,
            NoCustomInstructions,
            CopilotHome);

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

    private static void AppendList(
        System.Text.StringBuilder target,
        IReadOnlyList<string> values)
    {
        target.Append(values.Count).Append(':');
        foreach (var value in values)
            AppendValue(target, value);
    }

    private static void AppendValue(
        System.Text.StringBuilder target,
        string? value)
    {
        if (value is null)
        {
            target.Append("-1:");
            return;
        }
        target.Append(value.Length).Append(':').Append(value);
    }
}
