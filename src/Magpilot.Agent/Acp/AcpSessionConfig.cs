using System.Text.Json.Nodes;
using System.Runtime.ExceptionServices;

namespace Magpilot.Agent.Acp;

/// <summary>
/// Applies caller-requested session configuration using ACP's advertised
/// config options. Option ids and values are discovered from the session
/// response rather than assumed to be stable across Copilot CLI versions.
/// </summary>
internal static class AcpSessionConfig
{
    /// <summary>
    /// Outcome of an apply pass: the last complete config state observed plus the
    /// exact advertised value ids that were pinned, so the caller can re-verify
    /// the whole tuple against its own latest snapshot before it routes traffic.
    /// </summary>
    internal readonly record struct AppliedConfig(
        JsonNode? State,
        string? Model,
        string? ReasoningEffort);

    /// <summary>
    /// Tracks whether the session's configuration may have been mutated without
    /// being confirmed. It is raised immediately BEFORE a
    /// <c>session/set_config_option</c> request goes out and lowered only once a
    /// response has been verified (or a rollback has been verified). A caller
    /// cancellation that lands before any request therefore leaves it false, which
    /// is what lets the session manager distinguish "nothing happened, safe to
    /// retry" from "the live route may no longer match what we advertise".
    /// </summary>
    internal sealed class ApplyProgress
    {
        internal bool Indeterminate { get; private set; }

        internal void EnterMutation() => Indeterminate = true;

        internal void Confirmed() => Indeterminate = false;
    }

    internal static async Task<AppliedConfig> ApplyRequestedAsync(
        JsonNode? sessionResult,
        string sessionId,
        string? model,
        string? reasoningEffort,
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> callAsync,
        CancellationToken ct,
        ApplyProgress? progress = null)
    {
        progress ??= new ApplyProgress();
        var configState = sessionResult;
        string? priorModelValue = null;
        string? appliedModel = null;
        string? appliedReasoning = null;

        if (!string.IsNullOrWhiteSpace(model))
        {
            priorModelValue = GetCurrentSelectValue(
                configState,
                category: "model",
                description: "model",
                requestedValue: model,
                IsModelOption);
            (configState, appliedModel) = await SetSelectOptionAsync(
                configState,
                sessionId,
                requestedValue: model,
                category: "model",
                description: "model",
                IsModelOption,
                callAsync,
                progress,
                ct);
        }

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            try
            {
                (configState, appliedReasoning) = await SetSelectOptionAsync(
                    configState,
                    sessionId,
                    requestedValue: reasoningEffort,
                    category: "thought_level",
                    description: "reasoning effort",
                    IsReasoningOption,
                    callAsync,
                    progress,
                    ct);
            }
            catch (Exception applyException) when (appliedModel is not null && priorModelValue is not null)
            {
                // Whether the reasoning attempt already mutated the child. A
                // successful model rollback proves nothing about the reasoning
                // option, so this must survive the rollback's own confirmation.
                var reasoningIndeterminate = progress.Indeterminate;
                try
                {
                    // The caller's cancellation token may be what interrupted the
                    // reasoning update. Rollback is compensating cleanup, so give
                    // it an independent token and restore the exact prior value id
                    // before surfacing the original failure.
                    (configState, _) = await SetSelectOptionAsync(
                        configState,
                        sessionId,
                        requestedValue: priorModelValue,
                        category: "model",
                        description: "model rollback",
                        IsModelOption,
                        callAsync,
                        progress,
                        CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    // Rollback failed too: the child may now be on the requested
                    // model, the prior one, or neither. Say so explicitly -- the
                    // manager quarantines the session on this signal rather than
                    // keep serving a route whose config it cannot vouch for.
                    throw new SessionConfigurationException(
                        $"{applyException.Message} Restoring prior model value '{priorModelValue}' also failed: " +
                        $"{rollbackException.Message} Root cause: {rollbackException.GetBaseException().Message}",
                        new AggregateException(applyException, rollbackException))
                    {
                        LeavesSessionIndeterminate = true,
                    };
                }

                if (reasoningIndeterminate)
                    progress.EnterMutation();

                ExceptionDispatchInfo.Capture(applyException).Throw();
                throw;
            }
        }

        return new AppliedConfig(configState, appliedModel, appliedReasoning);
    }

    private static async Task<(JsonNode? State, string Applied)> SetSelectOptionAsync(
        JsonNode? configState,
        string sessionId,
        string requestedValue,
        string category,
        string description,
        Func<JsonObject, bool> matchesOption,
        Func<string, JsonObject, CancellationToken, Task<JsonNode?>> callAsync,
        ApplyProgress progress,
        CancellationToken ct)
    {
        var options = GetConfigOptions(configState, description, requestedValue);

        var config = FindOption(options, category, matchesOption)
            ?? throw new SessionConfigurationException(
                $"ACP did not advertise a {description} config option, so '{requestedValue}' cannot be pinned.");

        var configId = config["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(configId))
        {
            throw new SessionConfigurationException(
                $"ACP advertised a {description} config option without an id, so '{requestedValue}' cannot be pinned.");
        }

        var value = FindSelectValue(config, requestedValue);
        if (value is null)
        {
            var advertised = AdvertisedSelectValues(config);
            throw new SessionConfigurationException(
                $"ACP {description} config option '{configId}' does not offer requested value '{requestedValue}'. " +
                $"Advertised values: {FormatAdvertisedValues(advertised)}.");
        }

        JsonNode? result;
        // From here until the response is verified the child's configuration is
        // indeterminate: the request may have been applied, partially applied, or
        // dropped. Raise the flag BEFORE the await so a cancellation mid-flight is
        // reported as indeterminate too.
        progress.EnterMutation();
        try
        {
            result = await callAsync(
                "session/set_config_option",
                new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["configId"] = configId,
                    ["value"] = value,
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SessionConfigurationException(
                $"ACP failed to pin {description} '{requestedValue}' using config option '{configId}'.",
                ex)
            {
                LeavesSessionIndeterminate = true,
            };
        }

        var applied = GetConfigOptions(result, description, requestedValue)
            .OfType<JsonObject>()
            .FirstOrDefault(option =>
                string.Equals(option["id"]?.GetValue<string>(), configId, StringComparison.Ordinal));
        if (applied is null ||
            !JsonValueEquals(applied["currentValue"], value))
        {
            throw new SessionConfigurationException(
                $"ACP did not confirm {description} '{requestedValue}' on config option '{configId}'.")
            {
                LeavesSessionIndeterminate = true,
            };
        }

        progress.Confirmed();
        return (result, value);
    }

    private static JsonObject? FindOption(
        JsonArray options,
        string category,
        Func<JsonObject, bool> matchesOption)
    {
        var objects = options.OfType<JsonObject>().ToList();

        // Categories are optional, but when present they are the protocol's
        // semantic signal and must win over a coincidental id/name match.
        return objects.FirstOrDefault(option =>
                   string.Equals(option["category"]?.GetValue<string>(), category, StringComparison.OrdinalIgnoreCase))
               ?? objects.FirstOrDefault(matchesOption);
    }

    private static string? FindSelectValue(JsonObject config, string requestedValue)
    {
        if (!string.Equals(config["type"]?.GetValue<string>(), "select", StringComparison.OrdinalIgnoreCase))
            return null;

        if (config["options"] is not JsonArray options)
            return null;

        var choices = options
            .OfType<JsonObject>()
            .Select(option => new
            {
                Value = option["value"]?.GetValue<string>(),
                Name = option["name"]?.GetValue<string>(),
            })
            .ToList();

        // Match priority is global, not per choice. An early canonicalized
        // display-name match must never beat a later exact protocol value.
        return choices.FirstOrDefault(option =>
                   string.Equals(option.Value, requestedValue, StringComparison.OrdinalIgnoreCase))
               ?.Value
            ?? choices.FirstOrDefault(option =>
                   string.Equals(option.Name, requestedValue, StringComparison.OrdinalIgnoreCase))
               ?.Value
            ?? choices.FirstOrDefault(option =>
                   option.Name is not null &&
                   Canonicalize(option.Name) == Canonicalize(requestedValue))
               ?.Value;
    }

    internal static (string? Model, string? ReasoningEffort) ReadCurrentValues(JsonArray options)
    {
        var model = FindOption(options, "model", IsModelOption);
        var reasoning = FindOption(options, "thought_level", IsReasoningOption);
        return (
            CurrentStringValue(model),
            CurrentStringValue(reasoning));
    }

    private static bool IsModelOption(JsonObject option)
    {
        if (string.Equals(option["category"]?.GetValue<string>(), "model", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsNamed(option, "reasoning", "thought", "thinking", "effort"))
            return false;

        return IsNamed(option, "model");
    }

    private static bool IsReasoningOption(JsonObject option)
    {
        var category = option["category"]?.GetValue<string>();
        if (string.Equals(category, "thought_level", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsNamed(option, "reasoning", "thought", "thinking", "effort");
    }

    private static bool IsNamed(JsonObject option, params string[] terms)
    {
        var id = option["id"]?.GetValue<string>() ?? "";
        var name = option["name"]?.GetValue<string>() ?? "";
        return terms.Any(term =>
            id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string Canonicalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool JsonValueEquals(JsonNode? currentValue, string expected) =>
        currentValue is JsonValue value &&
        value.TryGetValue<string>(out var actual) &&
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? GetCurrentSelectValue(
        JsonNode? configState,
        string category,
        string description,
        string requestedValue,
        Func<JsonObject, bool> matchesOption)
    {
        var options = GetConfigOptions(configState, description, requestedValue);
        return CurrentStringValue(FindOption(options, category, matchesOption));
    }

    private static string? CurrentStringValue(JsonObject? option) =>
        option?["currentValue"] is JsonValue value &&
        value.TryGetValue<string>(out var current)
            ? current
            : null;

    private static IReadOnlyList<string> AdvertisedSelectValues(JsonObject config) =>
        config["options"] is JsonArray options
            ? options
                .OfType<JsonObject>()
                .Select(option => option["value"]?.GetValue<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    private static string FormatAdvertisedValues(IReadOnlyList<string> values) =>
        values.Count == 0
            ? "(none)"
            : string.Join(", ", values.Select(static value => $"'{value}'"));

    private static JsonArray GetConfigOptions(JsonNode? configState, string description, string requestedValue)
    {
        if (configState?["configOptions"] is JsonArray options)
            return options;

        throw new SessionConfigurationException(
            $"ACP did not advertise configOptions, so the requested {description} '{requestedValue}' cannot be pinned.");
    }
}

internal sealed class SessionConfigurationException : InvalidOperationException
{
    internal SessionConfigurationException(string message) : base(message) { }

    internal SessionConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>
    /// True when the failure happened after a <c>session/set_config_option</c>
    /// request went out without a verified outcome, so the live session may no
    /// longer match either the requested or the previous configuration. The
    /// session manager quarantines on this rather than keep routing prompts to a
    /// child whose configuration it cannot vouch for.
    /// </summary>
    internal bool LeavesSessionIndeterminate { get; init; }

    /// <summary>
    /// The session that was already created or loaded before configuration
    /// failed. This lets an HTTP caller retain the id and retry configuration
    /// in place instead of creating a second session.
    /// </summary>
    internal string? SessionId { get; set; }
}
