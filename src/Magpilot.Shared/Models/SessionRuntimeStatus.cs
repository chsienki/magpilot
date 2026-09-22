namespace Magpilot.Shared.Models;

/// <summary>
/// Runtime-owned status for one attached Copilot session. Values that are not
/// available from the active backend remain null rather than being reported as
/// zero.
/// </summary>
public sealed record SessionRuntimeStatus(
    string Backend,
    string? ModelId,
    string? ModelName,
    string? ReasoningEffort,
    long? CurrentTokens,
    long? TokenLimit,
    double? AiCreditsUsed,
    bool CanEditModel,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One model choice advertised by the active Copilot runtime.
/// </summary>
public sealed record SessionModelOption(
    string Id,
    string Name,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort);
