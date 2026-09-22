using Magpilot.Shared.Models;

namespace Magpilot.Agent.Runtime;

internal static class SessionModelSelection
{
    public static string? ResolveReasoningEffort(
        string? requested,
        string? current,
        SessionModelOption model)
    {
        var supported = model.SupportedReasoningEfforts;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = supported.FirstOrDefault(effort =>
                string.Equals(effort, requested, StringComparison.OrdinalIgnoreCase));
            return match
                ?? throw new SessionModelUpdateException(
                    $"Model '{model.Id}' does not support reasoning effort '{requested}'.");
        }

        return supported.FirstOrDefault(effort =>
                   string.Equals(effort, current, StringComparison.OrdinalIgnoreCase))
            ?? model.DefaultReasoningEffort;
    }
}
