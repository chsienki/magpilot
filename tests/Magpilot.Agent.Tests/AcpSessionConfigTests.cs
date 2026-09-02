using System.Text.Json.Nodes;
using Magpilot.Agent.Acp;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class AcpSessionConfigTests
{
    [Fact]
    public async Task ApplyRequestedAsync_pins_agent_before_model_and_reasoning()
    {
        var calls = new List<JsonObject>();
        var initial = SessionResult(
            SelectOption("selected-agent", "Agent", "_agent", "default", ("default", "Default"), ("phone", "magnus-phone")),
            SelectOption("selected-model", "Model", "model", "default", ("default", "Default"), ("fast", "gpt-5.6-sol-fast")),
            SelectOption("thinking", "Reasoning effort", "thought_level", "high", ("none", "None"), ("high", "High")));

        var applied = await AcpSessionConfig.ApplyRequestedAsync(
            initial,
            "session-123",
            "magnus-phone",
            "gpt-5.6-sol-fast",
            "none",
            (method, @params, _) =>
            {
                Assert.Equal("session/set_config_option", method);
                calls.Add((JsonObject)@params.DeepClone());
                return Task.FromResult<JsonNode?>(calls.Count switch
                {
                    1 => SessionResult(
                        SelectOption("selected-agent", "Agent", "_agent", "phone", ("default", "Default"), ("phone", "magnus-phone")),
                        SelectOption("selected-model", "Model", "model", "default", ("default", "Default"), ("fast", "gpt-5.6-sol-fast")),
                        SelectOption("thinking", "Reasoning effort", "thought_level", "high", ("none", "None"), ("high", "High"))),
                    2 => SessionResult(
                        SelectOption("selected-agent", "Agent", "_agent", "phone", ("default", "Default"), ("phone", "magnus-phone")),
                        SelectOption("selected-model", "Model", "model", "fast", ("default", "Default"), ("fast", "gpt-5.6-sol-fast")),
                        SelectOption("thinking", "Reasoning effort", "thought_level", "high", ("none", "None"), ("high", "High"))),
                    3 => SessionResult(
                        SelectOption("selected-agent", "Agent", "_agent", "phone", ("default", "Default"), ("phone", "magnus-phone")),
                        SelectOption("selected-model", "Model", "model", "fast", ("default", "Default"), ("fast", "gpt-5.6-sol-fast")),
                        SelectOption("thinking", "Reasoning effort", "thought_level", "none", ("none", "None"), ("high", "High"))),
                    _ => throw new Xunit.Sdk.XunitException("Unexpected RPC"),
                });
            },
            CancellationToken.None);

        Assert.Collection(
            calls,
            call => Assert.Equal(("selected-agent", "phone"), ConfigSelection(call)),
            call => Assert.Equal(("selected-model", "fast"), ConfigSelection(call)),
            call => Assert.Equal(("thinking", "none"), ConfigSelection(call)));
        Assert.Equal("phone", applied.Agent);
        Assert.Equal("fast", applied.Model);
        Assert.Equal("none", applied.ReasoningEffort);
    }

    [Fact]
    public async Task ApplyRequestedAsync_pins_model_then_uses_updated_options_for_reasoning()
    {
        var calls = new List<JsonObject>();
        var initial = SessionResult(
            SelectOption("selected-model", "Model", "model", "default", ("default", "Default"), ("opus", "Claude Opus 4.8")));

        await AcpSessionConfig.ApplyRequestedAsync(
            initial,
            "session-123",
            null,
            "claude-opus-4.8",
            "none",
            (method, @params, _) =>
            {
                Assert.Equal("session/set_config_option", method);
                calls.Add((JsonObject)@params.DeepClone());
                return Task.FromResult<JsonNode?>(calls.Count == 1
                    ? SessionResult(
                        SelectOption("selected-model", "Model", "model", "opus", ("default", "Default"), ("opus", "claude-opus-4.8")),
                        SelectOption("thinking", "Reasoning effort", "thought_level", "high", ("none", "None"), ("high", "High")))
                    : SessionResult(
                        SelectOption("selected-model", "Model", "model", "opus", ("default", "Default"), ("opus", "claude-opus-4.8")),
                        SelectOption("thinking", "Reasoning effort", "thought_level", "none", ("none", "None"), ("high", "High"))));
            },
            CancellationToken.None);

        Assert.Collection(
            calls,
            call =>
            {
                Assert.Equal("session-123", call["sessionId"]?.GetValue<string>());
                Assert.Equal("selected-model", call["configId"]?.GetValue<string>());
                Assert.Equal("opus", call["value"]?.GetValue<string>());
            },
            call =>
            {
                Assert.Equal("session-123", call["sessionId"]?.GetValue<string>());
                Assert.Equal("thinking", call["configId"]?.GetValue<string>());
                Assert.Equal("none", call["value"]?.GetValue<string>());
            });
    }

    [Fact]
    public async Task ApplyRequestedAsync_discovers_options_without_categories()
    {
        var calls = new List<JsonObject>();
        var initial = SessionResult(
            SelectOption("copilot-model", "Model picker", null, "default", ("fast", "gpt-5.6-sol-fast")),
            SelectOption("reasoning_effort", "Effort", null, "high", ("minimal", "Minimal")));

        await AcpSessionConfig.ApplyRequestedAsync(
            initial,
            "session-123",
            null,
            "gpt-5.6-sol-fast",
            "minimal",
            (method, @params, _) =>
            {
                Assert.Equal("session/set_config_option", method);
                calls.Add((JsonObject)@params.DeepClone());
                var modelApplied = calls.Count >= 1;
                var effortApplied = calls.Count >= 2;
                return Task.FromResult<JsonNode?>(SessionResult(
                    SelectOption("copilot-model", "Model picker", null, modelApplied ? "fast" : "default", ("fast", "gpt-5.6-sol-fast")),
                    SelectOption("reasoning_effort", "Effort", null, effortApplied ? "minimal" : "high", ("minimal", "Minimal"))));
            },
            CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Equal("copilot-model", calls[0]["configId"]?.GetValue<string>());
        Assert.Equal("reasoning_effort", calls[1]["configId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApplyRequestedAsync_prefers_semantic_categories_over_heuristic_matches()
    {
        var calls = new List<JsonObject>();
        var initial = SessionResult(
            SelectOption("model-mode", "Model mode", null, "balanced", ("balanced", "Balanced")),
            SelectOption("selected-model", "Provider", "model", "default", ("opus", "claude-opus-4.8")));

        await AcpSessionConfig.ApplyRequestedAsync(
            initial,
            "session-123",
            null,
            "claude-opus-4.8",
            null,
            (_, @params, _) =>
            {
                calls.Add((JsonObject)@params.DeepClone());
                return Task.FromResult<JsonNode?>(SessionResult(
                    SelectOption("model-mode", "Model mode", null, "balanced", ("balanced", "Balanced")),
                    SelectOption("selected-model", "Provider", "model", "opus", ("opus", "claude-opus-4.8"))));
            },
            CancellationToken.None);

        var call = Assert.Single(calls);
        Assert.Equal("selected-model", call["configId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApplyRequestedAsync_without_requested_values_does_not_require_config_options()
    {
        await AcpSessionConfig.ApplyRequestedAsync(
            new JsonObject { ["sessionId"] = "session-123" },
            "session-123",
            null,
            null,
            null,
            (_, _, _) => throw new Xunit.Sdk.XunitException("RPC should not be called"),
            CancellationToken.None);
    }

    [Fact]
    public async Task ApplyRequestedAsync_fails_when_requested_model_option_is_missing()
    {
        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            AcpSessionConfig.ApplyRequestedAsync(
                SessionResult(
                    SelectOption("mode", "Mode", "mode", "ask", ("ask", "Ask"))),
                "session-123",
                null,
                "claude-opus-4.8",
                null,
                (_, _, _) => throw new Xunit.Sdk.XunitException("RPC should not be called"),
                CancellationToken.None));

        Assert.Contains("did not advertise a model config option", ex.Message);
    }

    [Fact]
    public async Task ApplyRequestedAsync_fails_when_requested_reasoning_value_is_missing()
    {
        var initial = SessionResult(
            SelectOption("thinking", "Reasoning effort", "thought_level", "high", ("high", "High")));

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            AcpSessionConfig.ApplyRequestedAsync(
                initial,
                "session-123",
                null,
                null,
                "minimal",
                (_, _, _) => throw new Xunit.Sdk.XunitException("RPC should not be called"),
                CancellationToken.None));

        Assert.Contains("does not offer requested value 'minimal'", ex.Message);
        Assert.Contains("Advertised values: 'high'", ex.Message);
    }

    [Fact]
    public async Task ApplyRequestedAsync_fails_when_agent_does_not_confirm_value()
    {
        var initial = SessionResult(
            SelectOption("model", "Model", "model", "default", ("opus", "claude-opus-4.8")));

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            AcpSessionConfig.ApplyRequestedAsync(
                initial,
                "session-123",
                null,
                "claude-opus-4.8",
                null,
                (_, _, _) => Task.FromResult<JsonNode?>(SessionResult(
                    SelectOption("model", "Model", "model", "default", ("opus", "claude-opus-4.8")))),
                CancellationToken.None));

        Assert.Contains("did not confirm model", ex.Message);
    }

    [Fact]
    public async Task ApplyRequestedAsync_prefers_exact_value_across_all_choices()
    {
        var initial = SessionResult(
            SelectOption(
                "model",
                "Model",
                "model",
                "default",
                ("canonical-first", "gpt 5.6 sol fast"),
                ("gpt-5.6-sol-fast", "Other")));

        JsonObject? sent = null;
        await AcpSessionConfig.ApplyRequestedAsync(
            initial,
            "session-123",
            null,
            "gpt-5.6-sol-fast",
            null,
            (_, @params, _) =>
            {
                sent = (JsonObject)@params.DeepClone();
                return Task.FromResult<JsonNode?>(SessionResult(
                    SelectOption(
                        "model",
                        "Model",
                        "model",
                        "gpt-5.6-sol-fast",
                        ("canonical-first", "gpt 5.6 sol fast"),
                        ("gpt-5.6-sol-fast", "Other"))));
            },
            CancellationToken.None);

        Assert.Equal("gpt-5.6-sol-fast", sent?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApplyRequestedAsync_prefers_exact_display_name_before_canonical_name()
    {
        var initial = SessionResult(
            SelectOption(
                "model",
                "Model",
                "model",
                "default",
                ("canonical-first", "Claude Opus 4.8"),
                ("exact-name", "claude-opus-4.8")));

        JsonObject? sent = null;
        await AcpSessionConfig.ApplyRequestedAsync(
            initial,
            "session-123",
            null,
            "claude-opus-4.8",
            null,
            (_, @params, _) =>
            {
                sent = (JsonObject)@params.DeepClone();
                return Task.FromResult<JsonNode?>(SessionResult(
                    SelectOption(
                        "model",
                        "Model",
                        "model",
                        "exact-name",
                        ("canonical-first", "Claude Opus 4.8"),
                        ("exact-name", "claude-opus-4.8"))));
            },
            CancellationToken.None);

        Assert.Equal("exact-name", sent?["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApplyRequestedAsync_rolls_model_and_agent_back_in_reverse_order_when_reasoning_fails()
    {
        var calls = new List<JsonObject>();
        var initial = SessionResult(
            SelectOption("agent", "Agent", "_agent", "default", ("default", "Default"), ("phone", "magnus-phone")),
            SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")));

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            AcpSessionConfig.ApplyRequestedAsync(
                initial,
                "session-123",
                "magnus-phone",
                "new",
                "none",
                (_, @params, _) =>
                {
                    calls.Add((JsonObject)@params.DeepClone());
                    return calls.Count switch
                    {
                        1 => Task.FromResult<JsonNode?>(SessionResult(
                            SelectOption("agent", "Agent", "_agent", "phone", ("default", "Default"), ("phone", "magnus-phone")),
                            SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
                            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))),
                        2 => Task.FromResult<JsonNode?>(SessionResult(
                            SelectOption("agent", "Agent", "_agent", "phone", ("default", "Default"), ("phone", "magnus-phone")),
                            SelectOption("model", "Model", "model", "new", ("old", "Old"), ("new", "New")),
                            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))),
                        3 => Task.FromException<JsonNode?>(new InvalidOperationException("reasoning rejected")),
                        4 => Task.FromResult<JsonNode?>(SessionResult(
                            SelectOption("agent", "Agent", "_agent", "phone", ("default", "Default"), ("phone", "magnus-phone")),
                            SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
                            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))),
                        5 => Task.FromResult<JsonNode?>(SessionResult(
                            SelectOption("agent", "Agent", "_agent", "default", ("default", "Default"), ("phone", "magnus-phone")),
                            SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
                            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))),
                        _ => throw new Xunit.Sdk.XunitException("Unexpected RPC"),
                    };
                },
                CancellationToken.None));

        Assert.Contains("failed to pin reasoning effort", ex.Message);
        Assert.Collection(
            calls,
            call => Assert.Equal(("agent", "phone"), ConfigSelection(call)),
            call => Assert.Equal(("model", "new"), ConfigSelection(call)),
            call => Assert.Equal(("thinking", "none"), ConfigSelection(call)),
            call => Assert.Equal(("model", "old"), ConfigSelection(call)),
            call => Assert.Equal(("agent", "default"), ConfigSelection(call)));
    }

    [Fact]
    public async Task ApplyRequestedAsync_includes_model_rollback_failure()
    {
        var callCount = 0;
        var initial = SessionResult(
            SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")));

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            AcpSessionConfig.ApplyRequestedAsync(
                initial,
                "session-123",
                null,
                "new",
                "none",
                (_, _, _) =>
                {
                    callCount++;
                    return callCount switch
                    {
                        1 => Task.FromResult<JsonNode?>(SessionResult(
                            SelectOption("model", "Model", "model", "new", ("old", "Old"), ("new", "New")),
                            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))),
                        2 => Task.FromException<JsonNode?>(new InvalidOperationException("reasoning rejected")),
                        3 => Task.FromException<JsonNode?>(new InvalidOperationException("rollback rejected")),
                        _ => throw new Xunit.Sdk.XunitException("Unexpected RPC"),
                    };
                },
                CancellationToken.None));

        Assert.Contains("Restoring prior configuration also failed", ex.Message);
        Assert.Contains("rollback rejected", ex.Message);
    }

    [Fact]
    public async Task ApplyRequestedAsync_reports_no_mutation_when_the_request_is_rejected_up_front()
    {
        var progress = new AcpSessionConfig.ApplyProgress();

        await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            AcpSessionConfig.ApplyRequestedAsync(
                SessionResult(SelectOption("model", "Model", "model", "old", ("old", "Old"))),
                "session-123",
                null,
                "new",
                null,
                (_, _, _) => throw new Xunit.Sdk.XunitException("RPC should not be called"),
                CancellationToken.None,
                progress));

        // Nothing left the agent, so the session's last verified
        // configuration is still the live one and it stays servable.
        Assert.False(progress.Indeterminate);
    }

    [Fact]
    public async Task ApplyRequestedAsync_keeps_indeterminate_when_reasoning_was_already_sent()
    {
        var progress = new AcpSessionConfig.ApplyProgress();
        var calls = 0;
        var initial = SessionResult(
            SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")));

        await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            AcpSessionConfig.ApplyRequestedAsync(
                initial,
                "session-123",
                null,
                "new",
                "none",
                (_, _, _) =>
                {
                    calls++;
                    return calls switch
                    {
                        // Reasoning is sent and comes back unconfirmed, so its real
                        // value is unknown; a successful model rollback afterwards
                        // says nothing about it.
                        2 => Task.FromResult<JsonNode?>(SessionResult(
                            SelectOption("model", "Model", "model", "new", ("old", "Old"), ("new", "New")),
                            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))),
                        _ => Task.FromResult<JsonNode?>(SessionResult(
                            SelectOption("model", "Model", "model", calls == 1 ? "new" : "old", ("old", "Old"), ("new", "New")),
                            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))),
                    };
                },
                CancellationToken.None,
                progress));

        Assert.Equal(3, calls);
        Assert.True(progress.Indeterminate);
    }

    [Fact]
    public async Task ApplyRequestedAsync_returns_the_advertised_values_it_pinned()
    {
        var initial = SessionResult(
            SelectOption("model", "Model", "model", "default", ("opus", "claude-opus-4.8")),
            SelectOption("thinking", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")));

        var applied = await AcpSessionConfig.ApplyRequestedAsync(
            initial,
            "session-123",
            null,
            "claude-opus-4.8",
            "none",
            (_, @params, _) => Task.FromResult<JsonNode?>(SessionResult(
                SelectOption("model", "Model", "model", "opus", ("opus", "claude-opus-4.8")),
                SelectOption(
                    "thinking",
                    "Reasoning",
                    "thought_level",
                    @params["configId"]?.GetValue<string>() == "thinking" ? "none" : "high",
                    ("none", "None"),
                    ("high", "High")))),
            CancellationToken.None);

        // The caller pins by display name but must be able to re-verify against
        // the protocol value the agent actually stores.
        Assert.Equal("opus", applied.Model);
        Assert.Equal("none", applied.ReasoningEffort);
    }

    private static JsonObject SessionResult(params JsonObject[] configOptions) =>
        new()
        {
            ["sessionId"] = "session-123",
            ["configOptions"] = new JsonArray(configOptions.Cast<JsonNode?>().ToArray()),
        };

    private static (string? ConfigId, string? Value) ConfigSelection(JsonObject call) =>
        (call["configId"]?.GetValue<string>(), call["value"]?.GetValue<string>());

    private static JsonObject SelectOption(
        string id,
        string name,
        string? category,
        string currentValue,
        params (string Value, string Name)[] values)
    {
        var option = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["type"] = "select",
            ["currentValue"] = currentValue,
            ["options"] = new JsonArray(values.Select(value => (JsonNode?)new JsonObject
            {
                ["value"] = value.Value,
                ["name"] = value.Name,
            }).ToArray()),
        };
        if (category is not null)
            option["category"] = category;
        return option;
    }
}
