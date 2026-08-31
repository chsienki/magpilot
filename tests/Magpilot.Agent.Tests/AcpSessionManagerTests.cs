using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Magpilot.Agent.Acp;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class AcpSessionManagerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"magpilot-acp-manager-{Guid.NewGuid():N}");

    [Fact]
    public async Task New_session_without_requested_config_is_usable_when_config_options_are_absent()
    {
        const string sid = "new-without-config-options";
        var attached = false;
        var prompted = false;
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult<JsonNode?>(new JsonObject { ["sessionId"] = sid });
                case "session/prompt":
                    prompted = true;
                    return Task.FromResult<JsonNode?>(
                        new JsonObject { ["stopReason"] = "end_turn" });
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });
        var manager = NewManager(() => client);

        var created = await manager.NewSessionAsync(
            _root,
            AcpFlavor.Default,
            CancellationToken.None,
            _ => attached = true);

        Assert.Equal(sid, created);
        Assert.True(attached);
        Assert.True(manager.IsAttached(sid));
        Assert.False(manager.IsQuarantined(sid));

        var prompt = await manager.StartPromptAsync(
            sid,
            "hello",
            CancellationToken.None,
            CancellationToken.None);
        await prompt.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(prompted);
    }

    [Fact]
    public async Task Load_session_without_requested_config_is_usable_when_config_options_are_absent()
    {
        const string sid = "load-without-config-options";
        var attached = false;
        var prompted = false;
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            switch (method)
            {
                case "session/load":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult<JsonNode?>(new JsonObject { ["sessionId"] = sid });
                case "session/prompt":
                    prompted = true;
                    return Task.FromResult<JsonNode?>(
                        new JsonObject { ["stopReason"] = "end_turn" });
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });
        var manager = NewManager(() => client);
        Directory.CreateDirectory(Path.Combine(_root, sid));

        await manager.LoadSessionAsync(
            sid,
            _root,
            AcpFlavor.Default,
            CancellationToken.None,
            _ => attached = true);

        Assert.True(attached);
        Assert.True(manager.IsAttached(sid));
        Assert.False(manager.IsQuarantined(sid));

        var prompt = await manager.StartPromptAsync(
            sid,
            "hello",
            CancellationToken.None,
            CancellationToken.None);
        await prompt.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(prompted);
    }

    [Theory]
    [InlineData("requested-model", null, "model")]
    [InlineData(null, "high", "reasoning effort")]
    public async Task Explicit_session_config_without_config_options_stays_fail_closed(
        string? model,
        string? reasoningEffort,
        string description)
    {
        const string sid = "requested-without-config-options";
        var attached = false;
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            Assert.Equal("session/new", method);
            CreateSessionLock(sid, Environment.ProcessId);
            return Task.FromResult<JsonNode?>(new JsonObject { ["sessionId"] = sid });
        });
        var manager = NewManager(() => client);

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            manager.NewSessionAsync(
                _root,
                AcpFlavor.Resolve(useAgency: false, model, reasoningEffort),
                CancellationToken.None,
                _ => attached = true));

        Assert.Contains($"requested {description}", ex.Message);
        Assert.Equal(sid, ex.SessionId);
        Assert.False(attached);
        Assert.True(manager.IsAttached(sid));
        Assert.True(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Retained_config_expectation_without_config_options_stays_fail_closed()
    {
        const string sid = "retained-config-without-options";
        var model = "old";
        var attached = false;

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", model, ("old", "Old"), ("new", "New")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult(Snapshot());
            }

            Assert.Equal("session/set_config_option", method);
            model = @params!["value"]!.GetValue<string>();
            return Task.FromResult(Snapshot());
        });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            Assert.Equal("session/load", method);
            CreateSessionLock(sid, Environment.ProcessId);
            return Task.FromResult<JsonNode?>(new JsonObject { ["sessionId"] = sid });
        });

        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            (_, expected, _) =>
            {
                Assert.Same(oldClient, expected);
                current = freshClient;
                return Task.FromResult<AcpClient?>(freshClient);
            });

        await manager.NewSessionAsync(
            _root,
            AcpFlavor.Resolve(useAgency: false, model: "new", reasoningEffort: null),
            CancellationToken.None);
        Assert.Equal("new", manager.EffectiveFlavor(sid)!.Model);

        await manager.ForceDetachAsync(sid, CancellationToken.None);
        Assert.False(manager.IsAttached(sid));

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            manager.LoadSessionAsync(
                sid,
                _root,
                AcpFlavor.Default,
                CancellationToken.None,
                _ => attached = true));

        Assert.Equal(sid, ex.SessionId);
        Assert.False(attached);
        Assert.True(manager.IsAttached(sid));
        Assert.True(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task New_session_config_cancellation_quarantines_the_route_for_in_place_retry()
    {
        const string sid = "new-config-cancelled";
        var sets = 0;
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New"))));
            }

            Assert.Equal("session/set_config_option", method);
            // First attempt is cut short mid-flight: the child may or may not have
            // applied it, so the session must not be served afterwards.
            if (Interlocked.Increment(ref sets) == 1)
                return Task.FromCanceled<JsonNode?>(new CancellationToken(canceled: true));
            return Task.FromResult<JsonNode?>(ConfigState(
                sid,
                SelectOption("model", "Model", "model", "new", ("old", "Old"), ("new", "New"))));
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.CreateAsync(_root, useAgency: false, CancellationToken.None, model: "new"));

        // The child really does hold the session, so its lock stays; what changes
        // is that the session is quarantined and refused for traffic.
        Assert.True(File.Exists(LockPath(sid, Environment.ProcessId)));
        Assert.True(manager.IsQuarantined(sid));
        Assert.DoesNotContain(sid, registry.Owned);
        Assert.Equal(SessionState.Locked, registry.Get(sid)!.State);

        var retried = await registry.AdoptAsync(sid, force: false, CancellationToken.None, model: "new");

        Assert.Equal(SessionState.Owned, retried.State);
        Assert.False(manager.IsQuarantined(sid));
        Assert.Equal(2, sets);
    }

    [Fact]
    public async Task Load_session_config_failure_keeps_a_quarantined_route_that_retries_without_reloading()
    {
        const string sid = "load-config-failed";
        var loads = 0;
        var advertised = new[] { ("old", "Old") };
        var client = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/load")
            {
                Interlocked.Increment(ref loads);
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", advertised)));
            }

            Assert.Equal("session/set_config_option", method);
            return Task.FromResult<JsonNode?>(ConfigState(
                sid,
                SelectOption("model", "Model", "model", @params!["value"]!.GetValue<string>(), advertised)));
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);
        Directory.CreateDirectory(Path.Combine(_root, sid));
        File.WriteAllText(Path.Combine(_root, sid, "events.jsonl"), "{}");

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            registry.AdoptAsync(sid, force: false, CancellationToken.None, model: "new"));

        Assert.Contains("does not offer requested value 'new'", ex.Message);
        Assert.Equal(sid, ex.SessionId);
        Assert.True(manager.IsQuarantined(sid));
        Assert.DoesNotContain(sid, registry.Owned);
        Assert.True(File.Exists(LockPath(sid, Environment.ProcessId)));

        // The retry must go through the in-place config path: a second
        // session/load would only be told the session is already loaded.
        var retried = await registry.AdoptAsync(sid, force: false, CancellationToken.None, model: "old");

        Assert.Equal(SessionState.Owned, retried.State);
        Assert.False(manager.IsQuarantined(sid));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task Owned_config_failure_before_any_mutation_leaves_the_session_usable()
    {
        const string sid = "owned-preflight-failure";
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }

            throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        Assert.False(manager.IsQuarantined(sid));

        await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            registry.AdoptAsync(sid, force: false, CancellationToken.None, model: "unavailable"));

        // Nothing was sent to the child, so its last verified configuration
        // still stands and the session keeps serving.
        Assert.False(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Owned_config_cancelled_before_any_mutation_does_not_quarantine()
    {
        const string sid = "owned-cancelled-early";
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }

            throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.ApplyOwnedConfigurationAsync(
                sid,
                AcpFlavor.Resolve(useAgency: false, "old", null),
                processScopeSpecified: false,
                cancelled.Token));

        Assert.False(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Owned_config_requests_for_one_session_do_not_interleave()
    {
        const string sid = "owned-serialised";
        var concurrent = 0;
        var maxConcurrent = 0;
        var client = new FakeAcpClient(Environment.ProcessId, async (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return ConfigState(sid, SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")));
            }

            Assert.Equal("session/set_config_option", method);
            var running = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, running);
            try
            {
                await Task.Delay(25);
                return ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", @params!["value"]!.GetValue<string>(), ("old", "Old"), ("new", "New")));
            }
            finally { Interlocked.Decrement(ref concurrent); }
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        var requested = AcpFlavor.Resolve(useAgency: false, "new", null);

        await Task.WhenAll(
            manager.ApplyOwnedConfigurationAsync(sid, requested, false, CancellationToken.None),
            manager.ApplyOwnedConfigurationAsync(sid, requested, false, CancellationToken.None));

        Assert.Equal(1, maxConcurrent);
        Assert.False(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Prompt_waits_until_the_complete_configuration_tuple_is_verified()
    {
        const string sid = "prompt-after-config";
        var model = "old";
        var effort = "high";
        var modelSetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowModelSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompted = new TaskCompletionSource<(string Model, string Effort)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", model, ("old", "Old"), ("new", "New")),
            SelectOption("thought", "Reasoning", "thought_level", effort, ("none", "None"), ("high", "High")));

        var client = new FakeAcpClient(Environment.ProcessId, async (method, @params, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Snapshot();
                case "session/set_config_option":
                    if (@params!["configId"]!.GetValue<string>() == "model")
                    {
                        modelSetStarted.TrySetResult();
                        await allowModelSet.Task;
                        model = @params["value"]!.GetValue<string>();
                    }
                    else
                    {
                        effort = @params["value"]!.GetValue<string>();
                    }
                    return Snapshot();
                case "session/prompt":
                    prompted.TrySetResult((model, effort));
                    return new JsonObject { ["stopReason"] = "end_turn" };
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        var configure = manager.ApplyOwnedConfigurationAsync(
            sid,
            AcpFlavor.Resolve(useAgency: false, "new", "none"),
            processScopeSpecified: false,
            CancellationToken.None);
        await modelSetStarted.Task;

        var prompt = manager.PromptAsync(sid, "hello", CancellationToken.None);
        await Task.Delay(25);
        Assert.False(prompted.Task.IsCompleted);

        allowModelSet.TrySetResult();
        await Task.WhenAll(configure, prompt);

        Assert.Equal(("new", "none"), await prompted.Task);
    }

    [Fact]
    public async Task Shared_child_recycle_refuses_while_a_cohosted_session_is_configuring()
    {
        var created = 0;
        var recycleCalls = 0;
        var setStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var models = new ConcurrentDictionary<string, string>();

        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption(
                "model",
                "Model",
                "model",
                models.GetValueOrDefault(sid, "old"),
                ("old", "Old"),
                ("new", "New")));

        var client = new FakeAcpClient(Environment.ProcessId, async (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                var sid = $"config-busy-{Interlocked.Increment(ref created)}";
                models[sid] = "old";
                CreateSessionLock(sid, Environment.ProcessId);
                return Snapshot(sid);
            }

            Assert.Equal("session/set_config_option", method);
            var targetSid = @params!["sessionId"]!.GetValue<string>();
            setStarted.TrySetResult();
            await allowSet.Task;
            models[targetSid] = @params["value"]!.GetValue<string>();
            return Snapshot(targetSid);
        });
        var manager = NewManager(
            () => client,
            _ =>
            {
                Interlocked.Increment(ref recycleCalls);
                return client;
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        var configuring = manager.ApplyOwnedConfigurationAsync(
            "config-busy-1",
            AcpFlavor.Resolve(useAgency: false, "new", null),
            processScopeSpecified: false,
            CancellationToken.None);
        await setStarted.Task;

        var outcome = await manager.RecycleForStaleAsync(
            "config-busy-2",
            _ => _root,
            CancellationToken.None);

        Assert.Equal(RecycleOutcome.Busy, outcome);
        Assert.Equal(0, recycleCalls);
        Assert.True(manager.IsAttached("config-busy-1"));
        Assert.True(manager.IsAttached("config-busy-2"));

        allowSet.TrySetResult();
        await configuring;
        Assert.False(manager.IsQuarantined("config-busy-1"));
    }

    [Fact]
    public async Task Reserved_prompt_prevents_recycling_its_shared_child()
    {
        var created = 0;
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var client = new FakeAcpClient(Environment.ProcessId, async (method, @params, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                {
                    var sid = $"reserved-{Interlocked.Increment(ref created)}";
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Snapshot(sid);
                }
                case "session/prompt":
                    promptStarted.TrySetResult();
                    await finishPrompt.Task;
                    return new JsonObject { ["stopReason"] = "end_turn" };
                default:
                    return Snapshot(@params!["sessionId"]!.GetValue<string>());
            }
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        var prompt = await manager.StartPromptAsync(
            "reserved-2",
            "hello",
            CancellationToken.None,
            CancellationToken.None);
        await promptStarted.Task;

        var outcome = await manager.RecycleForStaleAsync(
            "reserved-1",
            _ => _root,
            CancellationToken.None);

        Assert.Equal(RecycleOutcome.Busy, outcome);
        Assert.True(manager.IsAttached("reserved-1"));
        Assert.True(manager.IsAttached("reserved-2"));

        finishPrompt.TrySetResult();
        await prompt;
    }

    [Fact]
    public async Task Prompt_reservation_rejects_a_retired_route_while_replacement_starts()
    {
        const string sid = "cancelled-reservation";
        var recycleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            Assert.Equal("session/new", method);
            CreateSessionLock(sid, Environment.ProcessId);
            return Task.FromResult(Snapshot());
        });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
            method switch
            {
                "session/load" => Task.FromResult(Snapshot()),
                "session/set_config_option" => Task.FromResult(Snapshot()),
                "session/prompt" => Task.FromResult<JsonNode?>(
                    new JsonObject { ["stopReason"] = "end_turn" }),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}"),
            });
        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            async (_, _) =>
            {
                recycleEntered.TrySetResult();
                await finishRecycle.Task;
                current = freshClient;
                return freshClient;
            });

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);
        var recycle = manager.RecycleForStaleAsync(sid, _ => _root, CancellationToken.None);
        await recycleEntered.Task;

        var reservation = manager.StartPromptAsync(
            sid,
            "hello",
            CancellationToken.None,
            CancellationToken.None);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            async () => await reservation.WaitAsync(TimeSpan.FromSeconds(2)));

        finishRecycle.TrySetResult();
        Assert.Equal(
            RecycleOutcome.Recycled,
            await recycle.WaitAsync(TimeSpan.FromSeconds(2)));

        var prompt = await manager.StartPromptAsync(
            sid,
            "hello again",
            CancellationToken.None,
            CancellationToken.None);
        await prompt.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Null_prompt_is_rejected_before_reserving_the_session_gate()
    {
        const string sid = "null-prompt";
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }

            Assert.Equal("session/prompt", method);
            return Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        });
        var manager = NewManager(() => client);

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            manager.StartPromptAsync(
                sid,
                null!,
                CancellationToken.None,
                CancellationToken.None));

        var prompt = await manager.StartPromptAsync(
            sid,
            "still usable",
            CancellationToken.None,
            CancellationToken.None);
        await prompt.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Draining_session_rejects_new_prompt_reservations_until_handoff_finishes()
    {
        const string sid = "draining-prompt";
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }

            Assert.Equal("session/prompt", method);
            return Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        });
        var manager = NewManager(() => client);

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);
        await manager.BeginSessionDrainAsync(sid, CancellationToken.None);
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.StartPromptAsync(
                    sid,
                    "too late",
                    CancellationToken.None,
                    CancellationToken.None));
            Assert.Contains("draining for host handoff", ex.Message);
        }
        finally
        {
            await manager.EndSessionDrainAsync(sid);
        }

        var prompt = await manager.StartPromptAsync(
            sid,
            "accepted again",
            CancellationToken.None,
            CancellationToken.None);
        await prompt.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancelled_prompt_keeps_the_session_lease_until_the_child_confirms_a_boundary()
    {
        const string sid = "cancelled-prompt-boundary";
        var promptCalls = 0;
        var firstPrompt = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelNotified = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }

            Assert.Equal("session/prompt", method);
            return Interlocked.Increment(ref promptCalls) == 1
                ? firstPrompt.Task
                : Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        }, notify: (method, _) =>
        {
            Assert.Equal("session/cancel", method);
            cancelNotified.TrySetResult();
            return Task.CompletedTask;
        });
        var manager = NewManager(() => client);

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);

        using var cancelled = new CancellationTokenSource();
        var running = await manager.StartPromptAsync(
            sid,
            "first",
            CancellationToken.None,
            cancelled.Token);
        await cancelled.CancelAsync();
        await cancelNotified.Task;

        var secondReservation = manager.StartPromptAsync(
            sid,
            "second",
            CancellationToken.None,
            CancellationToken.None);
        await Task.Delay(25);
        Assert.False(secondReservation.IsCompleted);

        firstPrompt.TrySetResult(new JsonObject { ["stopReason"] = "cancelled" });
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        var second = await secondReservation.WaitAsync(TimeSpan.FromSeconds(2));
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, promptCalls);
    }

    [Fact]
    public async Task Blocked_replacement_start_releases_routing_without_reusing_the_retired_client()
    {
        const string recycledSid = "routing-recycled";
        const string unrelatedSid = "routing-unrelated";
        const string lateSid = "routing-late-load";
        var isolatedFlavor = AcpFlavor.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            disableMcpServers: ["isolated"]);
        var lateLoadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateLoad = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStartBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReplacementStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unrelatedPromptStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retiredPromptCalls = 0;

        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, async (method, @params, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(recycledSid, Environment.ProcessId);
                    return Snapshot(recycledSid);
                case "session/load":
                    Assert.Equal(lateSid, @params!["sessionId"]!.GetValue<string>());
                    lateLoadStarted.TrySetResult();
                    await allowLateLoad.Task;
                    return Snapshot(lateSid);
                case "session/prompt":
                    Interlocked.Increment(ref retiredPromptCalls);
                    return new JsonObject { ["stopReason"] = "end_turn" };
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });
        var unrelatedClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(unrelatedSid, Environment.ProcessId);
                    return Task.FromResult(Snapshot(unrelatedSid));
                case "session/prompt":
                    unrelatedPromptStarted.TrySetResult();
                    return Task.FromResult<JsonNode?>(
                        new JsonObject { ["stopReason"] = "end_turn" });
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });
        var replacementClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            Assert.Equal("session/load", method);
            Assert.Equal(recycledSid, @params!["sessionId"]!.GetValue<string>());
            CreateSessionLock(recycledSid, Environment.ProcessId);
            return Task.FromResult(Snapshot(recycledSid));
        });

        AcpClient currentDefault = oldClient;
        var yolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
        var manager = new AcpSessionManager(
            (flavor, _) => Task.FromResult(
                flavor.Key == isolatedFlavor.Key ? unrelatedClient : currentDefault),
            async (flavor, expected, _) =>
            {
                Assert.Equal(AcpFlavor.Default.Key, flavor.Key);
                Assert.Same(oldClient, expected);
                replacementStartBlocked.TrySetResult();
                await allowReplacementStart.Task;
                currentDefault = replacementClient;
                return replacementClient;
            },
            yolo,
            NullLogger<AcpSessionManager>.Instance,
            _root);

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);
        await manager.NewSessionAsync(_root, isolatedFlavor, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_root, lateSid));

        var lateLoad = manager.LoadSessionAsync(
            lateSid,
            _root,
            AcpFlavor.Default,
            CancellationToken.None);
        await lateLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var recycle = manager.RecycleForStaleAsync(
            recycledSid,
            _ => _root,
            CancellationToken.None);
        await replacementStartBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        allowLateLoad.TrySetResult();
        var unrelatedReservation = manager.StartPromptAsync(
            unrelatedSid,
            "unrelated work",
            CancellationToken.None,
            CancellationToken.None);
        var retiredReservation = manager.StartPromptAsync(
            recycledSid,
            "must not reach the retired child",
            CancellationToken.None,
            CancellationToken.None);

        var lateCompletion = lateLoad.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var retiredCompletion = retiredReservation.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var workThatMustNotWaitForReplacement = Task.WhenAll(
            unrelatedPromptStarted.Task,
            lateCompletion,
            retiredCompletion);

        bool completedBeforeReplacement;
        try
        {
            completedBeforeReplacement = ReferenceEquals(
                await Task.WhenAny(
                    workThatMustNotWaitForReplacement,
                    Task.Delay(TimeSpan.FromSeconds(2))),
                workThatMustNotWaitForReplacement);
        }
        finally
        {
            allowReplacementStart.TrySetResult();
        }

        Assert.Equal(
            RecycleOutcome.Recycled,
            await recycle.WaitAsync(TimeSpan.FromSeconds(2)));
        var unrelatedPrompt = await unrelatedReservation.WaitAsync(TimeSpan.FromSeconds(2));
        await unrelatedPrompt.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<SessionConfigurationException>(() => lateLoad);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            async () => await retiredReservation);

        Assert.True(
            completedBeforeReplacement,
            "Unrelated routing and retired-client rejection waited for replacement initialization.");
        Assert.Equal(0, retiredPromptCalls);
        Assert.False(manager.IsAttached(lateSid));
        Assert.True(manager.IsAttached(recycledSid));
    }

    [Fact]
    public async Task Recycle_failure_after_disposal_invalidates_every_cohosted_route_and_allows_retry()
    {
        var created = 0;
        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            Assert.Equal("session/new", method);
            var sid = $"recycle-failure-{Interlocked.Increment(ref created)}";
            CreateSessionLock(sid, Environment.ProcessId);
            return Task.FromResult(Snapshot(sid));
        });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            var sid = @params!["sessionId"]!.GetValue<string>();
            if (method == "session/load")
                CreateSessionLock(sid, Environment.ProcessId);
            else
                Assert.Equal("session/set_config_option", method);
            return Task.FromResult(Snapshot(sid));
        });

        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            async (_, _) =>
            {
                await oldClient.DisposeAsync();
                current = freshClient;
                throw new OperationCanceledException("replacement startup cancelled");
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            manager.RecycleForStaleAsync(
                "recycle-failure-1",
                _ => _root,
                CancellationToken.None));

        Assert.False(manager.IsAttached("recycle-failure-1"));
        Assert.False(manager.IsAttached("recycle-failure-2"));
        Assert.DoesNotContain("recycle-failure-1", registry.Owned);
        Assert.DoesNotContain("recycle-failure-2", registry.Owned);

        var retried = await registry.AdoptAsync(
            "recycle-failure-2",
            force: false,
            CancellationToken.None);

        Assert.Equal(SessionState.Owned, retried.State);
        Assert.True(manager.IsAttached("recycle-failure-2"));
    }

    [Fact]
    public async Task Cancelled_session_load_recycles_its_child_before_retry()
    {
        const string sid = "cancelled-load";
        var disposed = false;

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(
            Environment.ProcessId,
            (method, _, _, _) =>
            {
                Assert.Equal("session/load", method);
                return Task.FromCanceled<JsonNode?>(new CancellationToken(canceled: true));
            },
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            Assert.Equal("session/load", method);
            CreateSessionLock(sid, Environment.ProcessId);
            return Task.FromResult(Snapshot());
        });

        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            async (_, _) =>
            {
                await oldClient.DisposeAsync();
                current = freshClient;
                return freshClient;
            });
        var registry = NewRegistry(manager);
        Directory.CreateDirectory(Path.Combine(_root, sid));
        File.WriteAllText(Path.Combine(_root, sid, "events.jsonl"), "{}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.AdoptAsync(sid, force: false, CancellationToken.None));

        Assert.True(disposed);
        Assert.False(manager.IsAttached(sid));
        Assert.DoesNotContain(sid, registry.Owned);

        var retried = await registry.AdoptAsync(
            sid,
            force: false,
            CancellationToken.None);

        Assert.Equal(SessionState.Owned, retried.State);
        Assert.True(manager.IsAttached(sid));
    }

    [Fact]
    public async Task Stale_concurrent_recycle_cannot_kill_the_replacement_child()
    {
        var created = 0;
        var recycleCalls = 0;
        var freshDisposed = false;
        var firstRecycleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            Assert.Equal("session/new", method);
            var sid = $"generation-{Interlocked.Increment(ref created)}";
            CreateSessionLock(sid, Environment.ProcessId);
            return Task.FromResult(Snapshot(sid));
        });
        var freshClient = new FakeAcpClient(
            Environment.ProcessId,
            (method, @params, _, _) =>
            {
                var sid = @params!["sessionId"]!.GetValue<string>();
                if (method == "session/load")
                    CreateSessionLock(sid, Environment.ProcessId);
                else
                    Assert.Equal("session/set_config_option", method);
                return Task.FromResult(Snapshot(sid));
            },
            () =>
            {
                freshDisposed = true;
                return ValueTask.CompletedTask;
            });

        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            async (_, expected, _) =>
            {
                Assert.Same(oldClient, expected);
                if (Interlocked.Increment(ref recycleCalls) == 1)
                {
                    firstRecycleEntered.TrySetResult();
                    await allowFirstRecycle.Task;
                    current = freshClient;
                    return freshClient;
                }
                return null;
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        var first = manager.RecycleForStaleAsync("generation-1", _ => _root, CancellationToken.None);
        await firstRecycleEntered.Task;
        var staleSecond = manager.RecycleForStaleAsync("generation-2", _ => _root, CancellationToken.None);
        allowFirstRecycle.TrySetResult();

        Assert.Equal(RecycleOutcome.Recycled, await first);
        Assert.Equal(RecycleOutcome.Recycled, await staleSecond);
        Assert.Equal(2, recycleCalls);
        Assert.False(freshDisposed);
        Assert.True(manager.IsAttached("generation-1"));
        Assert.True(manager.IsAttached("generation-2"));
    }

    [Fact]
    public async Task Load_response_arriving_after_child_recycle_cannot_publish_a_dead_route()
    {
        const string existingSid = "attach-race-existing";
        const string loadingSid = "attach-race-loading";
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowOldLoadToReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, async (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(existingSid, Environment.ProcessId);
                return Snapshot(existingSid);
            }

            Assert.Equal("session/load", method);
            Assert.Equal(loadingSid, @params!["sessionId"]!.GetValue<string>());
            loadStarted.TrySetResult();
            await allowOldLoadToReturn.Task;
            return Snapshot(loadingSid);
        });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            var sid = @params!["sessionId"]!.GetValue<string>();
            if (method == "session/load")
                CreateSessionLock(sid, Environment.ProcessId);
            else
                Assert.Equal("session/set_config_option", method);
            return Task.FromResult(Snapshot(sid));
        });

        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            (_, _) =>
            {
                current = freshClient;
                return Task.FromResult<AcpClient>(freshClient);
            });

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_root, loadingSid));

        var lateLoad = manager.LoadSessionAsync(
            loadingSid,
            _root,
            AcpFlavor.Default,
            CancellationToken.None);
        await loadStarted.Task;

        Assert.Equal(
            RecycleOutcome.Recycled,
            await manager.RecycleForStaleAsync(
                existingSid,
                _ => _root,
                CancellationToken.None));

        allowOldLoadToReturn.TrySetResult();
        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() => lateLoad);
        Assert.Equal(loadingSid, ex.SessionId);
        Assert.False(manager.IsAttached(loadingSid));

        await manager.LoadSessionAsync(
            loadingSid,
            _root,
            AcpFlavor.Default,
            CancellationToken.None);
        Assert.True(manager.IsAttached(loadingSid));

        manager.ObserveConfigState(
            oldClient,
            loadingSid,
            ConfigState(
                loadingSid,
                SelectOption("model", "Model", "model", "stale", ("old", "Old"), ("stale", "Stale"))));
        Assert.Equal("old", manager.EffectiveFlavor(loadingSid)!.Model);
    }

    [Fact]
    public async Task Watchdog_recycles_when_every_in_flight_turn_on_the_child_is_stalled()
    {
        var created = 0;
        var pendingPrompts = new ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>>();

        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(
            Environment.ProcessId,
            (method, @params, _, _) =>
            {
                if (method == "session/new")
                {
                    var sid = $"stalled-{Interlocked.Increment(ref created)}";
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot(sid));
                }

                Assert.Equal("session/prompt", method);
                var promptSid = @params!["sessionId"]!.GetValue<string>();
                return pendingPrompts.GetOrAdd(
                    promptSid,
                    _ => new TaskCompletionSource<JsonNode?>(
                        TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            },
            () =>
            {
                foreach (var prompt in pendingPrompts.Values)
                    prompt.TrySetException(new IOException("child recycled"));
                return ValueTask.CompletedTask;
            });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            Assert.True(
                method is "session/load" or "session/set_config_option",
                $"Unexpected RPC {method}");
            return Task.FromResult(Snapshot(@params!["sessionId"]!.GetValue<string>()));
        });
        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            async (_, _) =>
            {
                await oldClient.DisposeAsync();
                current = freshClient;
                return freshClient;
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        var first = await manager.StartPromptAsync(
            "stalled-1",
            "one",
            CancellationToken.None,
            CancellationToken.None);
        var second = await manager.StartPromptAsync(
            "stalled-2",
            "two",
            CancellationToken.None,
            CancellationToken.None);

        var recovered = await manager.SweepStalledTurnsAsync(
            TimeSpan.Zero,
            _ => _root,
            CancellationToken.None);

        Assert.Equal(2, recovered);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEqual(
            manager.IsAttached("stalled-1"),
            manager.IsAttached("stalled-2"));
    }

    [Fact]
    public async Task Force_detach_recycles_the_child_instead_of_leaving_its_turn_running()
    {
        const string sid = "force-detach";
        var pendingPrompt = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = false;

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", "old", ("old", "Old")));

        var oldClient = new FakeAcpClient(
            Environment.ProcessId,
            (method, _, _, _) =>
            {
                if (method == "session/new")
                {
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot());
                }

                Assert.Equal("session/prompt", method);
                return pendingPrompt.Task;
            },
            () =>
            {
                disposed = true;
                pendingPrompt.TrySetException(new IOException("child recycled"));
                return ValueTask.CompletedTask;
            });
        var freshClient = new FakeAcpClient(
            Environment.ProcessId,
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("Fresh child should stay idle"));
        var manager = NewManager(
            () => oldClient,
            async (_, _) =>
            {
                await oldClient.DisposeAsync();
                return freshClient;
            });

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);
        var prompt = await manager.StartPromptAsync(
            sid,
            "hello",
            CancellationToken.None,
            CancellationToken.None);

        var flavor = await manager.ForceDetachAsync(sid, CancellationToken.None);

        Assert.NotNull(flavor);
        Assert.True(disposed);
        Assert.False(manager.IsAttached(sid));
        await prompt.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Force_detach_recycles_a_session_left_resident_by_failed_close()
    {
        const string sid = "force-detach-resident";
        var disposed = false;

        var oldClient = new FakeAcpClient(
            Environment.ProcessId,
            (method, _, _, _) =>
            {
                if (method == "session/new")
                {
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["sessionId"] = sid,
                    });
                }

                Assert.Equal("session/close", method);
                return Task.FromException<JsonNode?>(
                    new InvalidOperationException("session/close is unavailable"));
            },
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            });
        var freshClient = new FakeAcpClient(
            Environment.ProcessId,
            (_, _, _, _) => throw new Xunit.Sdk.XunitException("Fresh child should stay idle"));
        var manager = NewManager(
            () => oldClient,
            async (_, _) =>
            {
                await oldClient.DisposeAsync();
                return freshClient;
            });

        await manager.NewSessionAsync(_root, AcpFlavor.Default, CancellationToken.None);
        await manager.CloseAsync(sid, _root, CancellationToken.None);
        Assert.False(manager.IsAttached(sid));
        Assert.True(manager.IsResident(sid));

        var flavor = await manager.ForceDetachAsync(sid, CancellationToken.None);

        Assert.NotNull(flavor);
        Assert.True(disposed);
        Assert.False(manager.IsResident(sid));
    }

    [Fact]
    public async Task Owned_config_rejects_a_tuple_that_drifts_after_the_set_is_confirmed()
    {
        const string sid = "owned-drift";
        var client = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
                    SelectOption("thought", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High"))));
            }

            Assert.Equal("session/set_config_option", method);
            // Pinning reasoning is confirmed, but the same response shows the model
            // has slid back -- the child is no longer on the tuple we asked for.
            return Task.FromResult<JsonNode?>(@params!["configId"]!.GetValue<string>() == "model"
                ? ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "new", ("old", "Old"), ("new", "New")),
                    SelectOption("thought", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High")))
                : ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
                    SelectOption("thought", "Reasoning", "thought_level", "none", ("none", "None"), ("high", "High"))));
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            registry.AdoptAsync(sid, force: false, CancellationToken.None, model: "new", reasoningEffort: "none"));

        Assert.Contains("reports model 'old' after pinning 'new'", ex.Message);
        Assert.True(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Ordered_config_notification_cannot_be_overwritten_by_an_older_set_response()
    {
        const string sid = "owned-wire-order-drift";
        AcpSessionManager? manager = null;
        var client = new FakeAcpClient(
            Environment.ProcessId,
            (method, _, _, _) =>
            {
                if (method == "session/new")
                {
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult<JsonNode?>(ConfigState(
                        sid,
                        SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New"))));
                }

                Assert.Equal("session/set_config_option", method);
                // Model the real read-loop ordering: the set response advertised
                // "new", then a later config_option_update moved the child back
                // to "old" before the response continuation resumed.
                manager!.ObserveConfigState(
                    sid,
                    new JsonObject
                    {
                        ["sessionUpdate"] = "config_option_update",
                        ["configOptions"] = new JsonArray(
                            SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New"))),
                    });
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "new", ("old", "Old"), ("new", "New"))));
            },
            publishesOrderedConfigState: true);
        manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            registry.AdoptAsync(
                sid,
                force: false,
                CancellationToken.None,
                model: "new"));

        Assert.Contains("reports model 'old' after pinning 'new'", ex.Message);
        Assert.True(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Later_notification_uses_the_canonical_model_value_that_was_verified()
    {
        const string sid = "canonical-model";
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption(
                        "model",
                        "Model",
                        "model",
                        "default",
                        ("opus", "claude-opus-4.8"))));
            }

            Assert.Equal("session/set_config_option", method);
            return Task.FromResult<JsonNode?>(ConfigState(
                sid,
                SelectOption(
                    "model",
                    "Model",
                    "model",
                    "opus",
                    ("opus", "claude-opus-4.8"))));
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "claude-opus-4.8");

        manager.ObserveConfigState(
            sid,
            ConfigState(
                sid,
                SelectOption(
                    "model",
                    "Model",
                    "model",
                    "opus",
                    ("opus", "claude-opus-4.8"))));

        Assert.False(manager.IsQuarantined(sid));
        Assert.Equal("opus", manager.EffectiveFlavor(sid)!.Model);
    }

    [Fact]
    public async Task Reasoning_only_update_keeps_the_retained_model_expectation()
    {
        const string sid = "carry-model-expectation";
        var model = "old";
        var effort = "high";

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", model, ("old", "Old"), ("new", "New")),
            SelectOption("thought", "Reasoning", "thought_level", effort, ("none", "None"), ("high", "High")));

        var client = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult(Snapshot());
            }

            Assert.Equal("session/set_config_option", method);
            if (@params!["configId"]!.GetValue<string>() == "model")
                model = @params["value"]!.GetValue<string>();
            else
                effort = @params["value"]!.GetValue<string>();
            return Task.FromResult(Snapshot());
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "new");
        await manager.ApplyOwnedConfigurationAsync(
            sid,
            AcpFlavor.Resolve(useAgency: false, model: null, reasoningEffort: "none"),
            processScopeSpecified: false,
            CancellationToken.None);

        manager.ObserveConfigState(
            sid,
            ConfigState(
                sid,
                SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New")),
                SelectOption("thought", "Reasoning", "thought_level", "none", ("none", "None"), ("high", "High"))));

        Assert.True(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Reasoning_only_update_rejects_a_response_that_drifted_the_pinned_model()
    {
        const string sid = "partial-update-drift";
        var model = "old";
        var effort = "high";

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", model, ("old", "Old"), ("new", "New")),
            SelectOption("thought", "Reasoning", "thought_level", effort, ("none", "None"), ("high", "High")));

        var client = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult(Snapshot());
            }

            Assert.Equal("session/set_config_option", method);
            if (@params!["configId"]!.GetValue<string>() == "model")
            {
                model = @params["value"]!.GetValue<string>();
            }
            else
            {
                effort = @params["value"]!.GetValue<string>();
                model = "old";
            }
            return Task.FromResult(Snapshot());
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "new");

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            manager.ApplyOwnedConfigurationAsync(
                sid,
                AcpFlavor.Resolve(useAgency: false, model: null, reasoningEffort: "none"),
                processScopeSpecified: false,
                CancellationToken.None));

        Assert.Contains("reports model 'old' after pinning 'new'", ex.Message);
        Assert.True(manager.IsQuarantined(sid));
    }

    [Fact]
    public async Task Quarantined_session_is_refused_for_prompts_until_it_is_reconfigured()
    {
        const string sid = "quarantined-prompt";
        var advertised = new[] { ("old", "Old") };
        var prompted = false;
        var client = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult<JsonNode?>(ConfigState(
                        sid,
                        SelectOption("model", "Model", "model", "old", advertised)));
                case "session/prompt":
                    prompted = true;
                    return Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
                default:
                    return Task.FromResult<JsonNode?>(ConfigState(
                        sid,
                        SelectOption("model", "Model", "model", @params!["value"]!.GetValue<string>(), advertised)));
            }
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            registry.CreateAsync(_root, useAgency: false, CancellationToken.None, model: "new"));
        Assert.True(manager.IsQuarantined(sid));

        await manager.PromptAsync(sid, "hello", CancellationToken.None);
        Assert.False(prompted);

        await manager.ApplyOwnedConfigurationAsync(
            sid,
            AcpFlavor.Resolve(useAgency: false, "old", null),
            processScopeSpecified: false,
            CancellationToken.None);
        Assert.False(manager.IsQuarantined(sid));

        await manager.PromptAsync(sid, "hello", CancellationToken.None);
        Assert.True(prompted);
    }

    [Fact]
    public async Task Release_from_host_reloads_on_the_recorded_flavor_and_recycles_the_resident_child()
    {
        const string sid = "release-flavor";
        var oldSets = 0;
        var freshSets = 0;
        var freshLoads = 0;

        JsonNode? Snapshot(string model, string effort) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", model, ("old", "Old"), ("fast", "Fast")),
            SelectOption("thought", "Reasoning", "thought_level", effort, ("none", "None"), ("high", "High")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot("old", "high"));
                case "session/close":
                    return Task.FromException<JsonNode?>(
                        new InvalidOperationException("session/close is unavailable"));
                default:
                    Interlocked.Increment(ref oldSets);
                    return Task.FromResult(@params!["configId"]!.GetValue<string>() == "model"
                        ? Snapshot("fast", "high")
                        : Snapshot("fast", "none"));
            }
        });

        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/load")
            {
                Interlocked.Increment(ref freshLoads);
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult(Snapshot("old", "high"));
            }

            Interlocked.Increment(ref freshSets);
            return Task.FromResult(@params!["configId"]!.GetValue<string>() == "model"
                ? Snapshot("fast", "high")
                : Snapshot("fast", "none"));
        });

        AcpClient current = oldClient;
        AcpFlavor? recycledFlavor = null;
        var manager = NewManager(
            () => current,
            flavor =>
            {
                recycledFlavor = flavor;
                current = freshClient;
                return freshClient;
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "fast",
            reasoningEffort: "none",
            disableMcpServers: ["phone-only"]);
        Assert.Equal(2, oldSets);

        await registry.AcquireForHostAsync(sid, Environment.ProcessId, force: false, CancellationToken.None);
        Assert.DoesNotContain(sid, registry.Owned);

        var state = await registry.ReleaseFromHostAsync(sid, Environment.ProcessId, force: false, CancellationToken.None);

        // The child that still had the session in memory is recycled first, so the
        // reload genuinely re-reads what the launcher wrote while it was driving.
        Assert.NotNull(recycledFlavor);
        Assert.Equal(["phone-only"], recycledFlavor.DisabledMcpServers);
        Assert.Equal("fast", recycledFlavor.Model);
        Assert.Equal("none", recycledFlavor.ReasoningEffort);
        Assert.Equal(1, freshLoads);
        Assert.Equal(2, freshSets);
        Assert.Contains(sid, registry.Owned);
        Assert.False(manager.IsQuarantined(sid));
        Assert.Equal(SessionOwner.Agent, state.Owner);
    }

    [Fact]
    public async Task Detached_host_acquire_persists_the_effective_flavor_through_restart_and_release()
    {
        const string sid = "detached-host-flavor";
        var model = "old";
        var effort = "high";

        JsonNode? Snapshot(string currentModel, string currentEffort) => ConfigState(
            sid,
            SelectOption(
                "model",
                "Model",
                "model",
                currentModel,
                ("old", "Old"),
                ("fast", "Fast")),
            SelectOption(
                "thought",
                "Reasoning",
                "thought_level",
                currentEffort,
                ("none", "None"),
                ("high", "High")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot(model, effort));
                case "session/set_config_option":
                    if (@params!["configId"]!.GetValue<string>() == "model")
                        model = @params["value"]!.GetValue<string>();
                    else
                        effort = @params["value"]!.GetValue<string>();
                    return Task.FromResult(Snapshot(model, effort));
                case "session/close":
                    return Task.FromResult<JsonNode?>(new JsonObject());
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });
        var manager = NewManager(() => oldClient);
        var ownership = NewHostOwnership();
        var registry = NewRegistry(manager, ownership);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "fast",
            reasoningEffort: "none",
            disableMcpServers: ["phone-only"]);

        var detachedFlavor = await registry.DetachAsync(sid, CancellationToken.None);
        Assert.NotNull(detachedFlavor);
        Assert.Equal(["phone-only"], detachedFlavor.DisabledMcpServers);
        Assert.Equal("fast", detachedFlavor.Model);
        Assert.Equal("none", detachedFlavor.ReasoningEffort);
        Assert.False(manager.IsAttached(sid));
        Assert.False(manager.IsResident(sid));
        Assert.Equal("fast", manager.EffectiveFlavor(sid)!.Model);

        await registry.AcquireForHostAsync(
            sid,
            Environment.ProcessId,
            force: false,
            CancellationToken.None);

        var restartedOwnership = NewHostOwnership();
        await restartedOwnership.StartAsync(CancellationToken.None);
        try
        {
            var reloadedModel = "old";
            var reloadedEffort = "high";
            var setCalls = new List<(string ConfigId, string Value)>();
            AcpFlavor? acquiredFlavor = null;
            var freshClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
            {
                if (method == "session/load")
                {
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot(reloadedModel, reloadedEffort));
                }

                Assert.Equal("session/set_config_option", method);
                var configId = @params!["configId"]!.GetValue<string>();
                var value = @params["value"]!.GetValue<string>();
                setCalls.Add((configId, value));
                if (configId == "model")
                    reloadedModel = value;
                else
                    reloadedEffort = value;
                return Task.FromResult(Snapshot(reloadedModel, reloadedEffort));
            });
            var restartedYolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
            var restartedManager = new AcpSessionManager(
                (flavor, _) =>
                {
                    acquiredFlavor = flavor;
                    return Task.FromResult<AcpClient>(freshClient);
                },
                (_, _, _) => Task.FromResult<AcpClient?>(freshClient),
                restartedYolo,
                NullLogger<AcpSessionManager>.Instance,
                _root);
            var restartedRegistry = NewRegistry(restartedManager, restartedOwnership);

            var state = await restartedRegistry.ReleaseFromHostAsync(
                sid,
                Environment.ProcessId,
                force: false,
                CancellationToken.None);

            Assert.NotNull(acquiredFlavor);
            Assert.Equal(["phone-only"], acquiredFlavor.DisabledMcpServers);
            Assert.Equal("fast", acquiredFlavor.Model);
            Assert.Equal("none", acquiredFlavor.ReasoningEffort);
            Assert.Collection(
                setCalls,
                call => Assert.Equal(("model", "fast"), call),
                call => Assert.Equal(("thought", "none"), call));
            Assert.Equal(SessionOwner.Agent, state.Owner);
            Assert.Contains(sid, restartedRegistry.Owned);
            Assert.Equal(["phone-only"], restartedManager.EffectiveFlavor(sid)!.DisabledMcpServers);
            Assert.Equal("fast", restartedManager.EffectiveFlavor(sid)!.Model);
            Assert.Equal("none", restartedManager.EffectiveFlavor(sid)!.ReasoningEffort);
        }
        finally
        {
            await restartedOwnership.StopAsync(CancellationToken.None);
            restartedOwnership.Dispose();
            ownership.Dispose();
        }
    }

    [Fact]
    public async Task Second_host_cannot_overwrite_a_live_host_owner()
    {
        const string sid = "host-owner-conflict";
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }
            Assert.Equal("session/close", method);
            return Task.FromResult<JsonNode?>(new JsonObject());
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.AcquireForHostAsync(
            sid,
            Environment.ProcessId,
            force: false,
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AcquireForHostAsync(
                sid,
                Environment.ProcessId + 1,
                force: true,
                CancellationToken.None));

        Assert.Contains($"host PID {Environment.ProcessId}", ex.Message);
        var state = registry.GetState(sid)!;
        Assert.Equal(SessionOwner.Host, state.Owner);
        Assert.Equal(Environment.ProcessId, state.HostPid);
    }

    [Fact]
    public async Task Polite_host_acquire_rejects_a_different_live_external_holder()
    {
        const string sid = "external-owner-conflict";
        var client = new FakeAcpClient(Environment.ProcessId, (_, _, _, _) =>
            throw new Xunit.Sdk.XunitException("ACP should not be called"));
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);
        CreateSessionLock(sid, Environment.ProcessId);
        File.WriteAllText(Path.Combine(_root, sid, "events.jsonl"), "{}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AcquireForHostAsync(
                sid,
                Environment.ProcessId + 1,
                force: false,
                CancellationToken.None));

        Assert.Contains($"external PID {Environment.ProcessId}", ex.Message);
        Assert.Equal(SessionOwner.External, registry.GetState(sid)!.Owner);
    }

    [Fact]
    public async Task Release_from_host_does_not_mark_owned_when_the_reload_fails()
    {
        const string sid = "release-reload-failed";
        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }
            return Task.FromResult<JsonNode?>(new JsonObject());
        });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
            method == "session/load"
                ? Task.FromException<JsonNode?>(new InvalidOperationException("session is already loaded"))
                : throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}"));

        AcpClient current = oldClient;
        var manager = NewManager(() => current, _ => { current = freshClient; return freshClient; });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.AcquireForHostAsync(sid, Environment.ProcessId, force: false, CancellationToken.None);

        var state = await registry.ReleaseFromHostAsync(sid, Environment.ProcessId, force: false, CancellationToken.None);

        Assert.DoesNotContain(sid, registry.Owned);
        Assert.NotEqual(SessionOwner.Agent, state.Owner);
    }

    [Fact]
    public async Task Release_from_host_keeps_host_ownership_when_configuration_fails_and_retries_in_place()
    {
        const string sid = "release-config-failed";
        var oldSets = 0;
        var freshLoads = 0;
        var freshSets = 0;

        JsonNode? Snapshot(string current) => ConfigState(
            sid,
            SelectOption("model", "Model", "model", current, ("old", "Old"), ("fast", "Fast")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot("old"));
                case "session/set_config_option":
                    Interlocked.Increment(ref oldSets);
                    return Task.FromResult(Snapshot("fast"));
                case "session/close":
                    return Task.FromException<JsonNode?>(
                        new InvalidOperationException("session/close is unavailable"));
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });

        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/load")
            {
                Interlocked.Increment(ref freshLoads);
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult(Snapshot("old"));
            }

            Assert.Equal("session/set_config_option", method);
            var attempt = Interlocked.Increment(ref freshSets);
            return Task.FromResult(Snapshot(attempt == 1 ? "old" : "fast"));
        });

        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            _ =>
            {
                current = freshClient;
                return freshClient;
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "fast");
        Assert.Equal(1, oldSets);
        await registry.AcquireForHostAsync(
            sid,
            Environment.ProcessId,
            force: false,
            CancellationToken.None);

        var failed = await registry.ReleaseFromHostAsync(
            sid,
            Environment.ProcessId,
            force: false,
            CancellationToken.None);

        Assert.Equal(1, freshLoads);
        Assert.Equal(1, freshSets);
        Assert.True(manager.IsAttached(sid));
        Assert.True(manager.IsQuarantined(sid));
        Assert.DoesNotContain(sid, registry.Owned);
        Assert.Equal(SessionOwner.Host, failed.Owner);

        var retried = await registry.ReleaseFromHostAsync(
            sid,
            Environment.ProcessId,
            force: false,
            CancellationToken.None);

        Assert.Equal(1, freshLoads);
        Assert.Equal(2, freshSets);
        Assert.False(manager.IsQuarantined(sid));
        Assert.Contains(sid, registry.Owned);
        Assert.Equal(SessionOwner.Agent, retried.Owner);
    }

    [Fact]
    public async Task Delayed_release_after_normal_adopt_cannot_restore_the_old_handoff_flavor()
    {
        const string sid = "stale-release";
        const int deadHostPid = 2147483646;
        var currentModel = "old";
        var freshSets = 0;

        JsonNode? Snapshot() => ConfigState(
            sid,
            SelectOption("model", "Model", "model", currentModel, ("old", "Old"), ("fast", "Fast")));

        var oldClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            switch (method)
            {
                case "session/new":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot());
                case "session/load":
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot());
                case "session/set_config_option":
                    currentModel = @params!["value"]!.GetValue<string>();
                    return Task.FromResult(Snapshot());
                case "session/close":
                    return Task.FromResult<JsonNode?>(new JsonObject());
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        });
        var freshClient = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/load")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult(Snapshot());
            }

            Assert.Equal("session/set_config_option", method);
            Interlocked.Increment(ref freshSets);
            currentModel = @params!["value"]!.GetValue<string>();
            return Task.FromResult(Snapshot());
        });

        AcpClient current = oldClient;
        var manager = NewManager(
            () => current,
            _ =>
            {
                current = freshClient;
                return freshClient;
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "fast");
        await registry.AcquireForHostAsync(
            sid,
            deadHostPid,
            force: false,
            CancellationToken.None);
        File.WriteAllText(Path.Combine(_root, sid, "events.jsonl"), "{}\n");

        var adopted = await registry.AdoptAsync(
            sid,
            force: false,
            CancellationToken.None,
            model: "old");
        Assert.Equal(SessionState.Owned, adopted.State);
        Assert.Equal("old", manager.EffectiveFlavor(sid)!.Model);
        var setsAfterAdopt = freshSets;

        var delayed = await registry.ReleaseFromHostAsync(
            sid,
            deadHostPid,
            force: false,
            CancellationToken.None);

        Assert.Equal(SessionOwner.Agent, delayed.Owner);
        Assert.Equal("old", manager.EffectiveFlavor(sid)!.Model);
        Assert.Equal(setsAfterAdopt, freshSets);
        Assert.Contains(sid, registry.Owned);
    }

    [Fact]
    public async Task Adopt_reattaches_a_session_whose_ownership_outlived_its_acp_route()
    {
        var created = 0;
        var loaded = new List<string>();
        var current = new Dictionary<string, string>();
        var loadedSessions = new HashSet<string>();
        var setsAfterReload = new List<(string SessionId, string Value)>();
        JsonNode? Snapshot(string sid) => ConfigState(
            sid,
            SelectOption(
                "model",
                "Model",
                "model",
                current.GetValueOrDefault(sid, "old"),
                ("old", "Old"),
                ("fast", "Fast")));

        Task<JsonNode?> HandleCall(string method, JsonObject? @params)
        {
            switch (method)
            {
                case "session/new":
                {
                    var sid = $"cohosted-{Interlocked.Increment(ref created)}";
                    current[sid] = "old";
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot(sid));
                }
                case "session/load":
                {
                    var sid = @params!["sessionId"]!.GetValue<string>();
                    lock (loaded) loaded.Add(sid);
                    loadedSessions.Add(sid);
                    current[sid] = "old";
                    CreateSessionLock(sid, Environment.ProcessId);
                    return Task.FromResult(Snapshot(sid));
                }
                case "session/set_config_option":
                {
                    var sid = @params!["sessionId"]!.GetValue<string>();
                    var value = @params["value"]!.GetValue<string>();
                    current[sid] = value;
                    if (loadedSessions.Contains(sid))
                        setsAfterReload.Add((sid, value));
                    return Task.FromResult(Snapshot(sid));
                }
                default:
                    throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
            }
        }

        var oldClient = new FakeAcpClient(
            Environment.ProcessId,
            (method, @params, _, _) => HandleCall(method, @params));
        var freshClient = new FakeAcpClient(
            Environment.ProcessId,
            (method, @params, _, _) => HandleCall(method, @params));
        AcpClient activeClient = oldClient;
        var manager = NewManager(
            () => activeClient,
            _ =>
            {
                activeClient = freshClient;
                return freshClient;
            });
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "fast");

        // Recycling for one session drops the co-hosted session's route too. Its
        // lock belongs to a live process so it is not reaped, leaving it reading
        // as Owned on disk with nothing behind it.
        await manager.RecycleForStaleAsync("cohosted-1", _ => _root, CancellationToken.None);
        Assert.False(manager.IsAttached("cohosted-2"));
        Assert.Equal(SessionState.Locked, registry.Get("cohosted-2")!.State);

        var result = await registry.AdoptAsync("cohosted-2", force: false, CancellationToken.None);

        Assert.Equal(SessionState.Owned, result.State);
        Assert.True(manager.IsAttached("cohosted-2"));
        Assert.Contains("cohosted-2", loaded);
        Assert.Contains(("cohosted-2", "fast"), setsAfterReload);
    }

    [Fact]
    public async Task Owned_adopt_applies_requested_config_from_latest_notification_state()
    {
        const string sid = "owned-adopt";
        var setCalls = new List<JsonObject>();
        var client = new FakeAcpClient(Environment.ProcessId, (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }

            Assert.Equal("session/set_config_option", method);
            setCalls.Add((JsonObject)@params!.DeepClone());
            return Task.FromResult<JsonNode?>(ConfigState(
                sid,
                SelectOption("model", "Model", "model", "new", ("old", "Old"), ("new", "New"))));
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(_root, useAgency: false, CancellationToken.None);
        manager.ObserveConfigState(
            sid,
            new JsonObject
            {
                ["sessionUpdate"] = "config_option_update",
                ["configOptions"] = new JsonArray(
                    SelectOption("model", "Model", "model", "old", ("old", "Old"), ("new", "New"))),
            });

        var result = await registry.AdoptAsync(
            sid,
            force: false,
            CancellationToken.None,
            model: "new");

        Assert.Equal(SessionState.Owned, result.State);
        var call = Assert.Single(setCalls);
        Assert.Equal("new", call["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task Owned_adopt_rejects_process_scope_change()
    {
        const string sid = "owned-scope";
        var client = new FakeAcpClient(Environment.ProcessId, (method, _, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, Environment.ProcessId);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"))));
            }

            throw new Xunit.Sdk.XunitException($"Unexpected RPC {method}");
        });
        var manager = NewManager(() => client);
        var registry = NewRegistry(manager);

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            disableMcpServers: ["phone-only"]);

        var ex = await Assert.ThrowsAsync<SessionConfigurationException>(() =>
            registry.AdoptAsync(
                sid,
                force: false,
                CancellationToken.None,
                disableMcpServers: ["other-scope"]));

        Assert.Contains("requires a different ACP child", ex.Message);
    }

    [Fact]
    public async Task Stale_recycle_uses_stored_flavor_and_reapplies_session_config()
    {
        const string sid = "stale-recycle";
        const int deadPid = 2147483646;
        var oldSetCount = 0;
        var freshSetCount = 0;

        var oldClient = new FakeAcpClient(deadPid, (method, @params, _, _) =>
        {
            if (method == "session/new")
            {
                CreateSessionLock(sid, deadPid);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "old", ("old", "Old"), ("fast", "Fast")),
                    SelectOption("thought", "Reasoning", "thought_level", "high", ("none", "None"), ("high", "High"))));
            }

            Assert.Equal("session/set_config_option", method);
            oldSetCount++;
            var configId = @params?["configId"]?.GetValue<string>();
            return Task.FromResult<JsonNode?>(ConfigState(
                sid,
                SelectOption("model", "Model", "model", "fast", ("old", "Old"), ("fast", "Fast")),
                SelectOption(
                    "thought",
                    "Reasoning",
                    "thought_level",
                    configId == "thought" ? "none" : "high",
                    ("none", "None"),
                    ("high", "High"))));
        });

        var freshClient = new FakeAcpClient(deadPid, (method, @params, _, _) =>
        {
            if (method == "session/load")
            {
                CreateSessionLock(sid, deadPid);
                return Task.FromResult<JsonNode?>(ConfigState(
                    sid,
                    SelectOption("model", "Model", "model", "fast", ("old", "Old"), ("fast", "Fast")),
                    SelectOption("thought", "Reasoning", "thought_level", "none", ("none", "None"), ("high", "High"))));
            }

            Assert.Equal("session/set_config_option", method);
            freshSetCount++;
            return Task.FromResult<JsonNode?>(ConfigState(
                sid,
                SelectOption("model", "Model", "model", "fast", ("old", "Old"), ("fast", "Fast")),
                SelectOption("thought", "Reasoning", "thought_level", "none", ("none", "None"), ("high", "High"))));
        });

        AcpClient current = oldClient;
        AcpFlavor? recycledFlavor = null;
        var manager = NewManager(
            () => current,
            flavor =>
            {
                recycledFlavor = flavor;
                current = freshClient;
                return freshClient;
            });
        var requested = AcpFlavor.Resolve(
            useAgency: false,
            model: "fast",
            reasoningEffort: "none",
            disableMcpServers: ["phone-only"]);

        await manager.NewSessionAsync(_root, requested, CancellationToken.None);
        var outcome = await manager.RecycleForStaleAsync(sid, _ => _root, CancellationToken.None);

        Assert.Equal(RecycleOutcome.Recycled, outcome);
        Assert.NotNull(recycledFlavor);
        Assert.Equal(requested.Key, recycledFlavor.Key);
        Assert.Equal(["phone-only"], recycledFlavor.DisabledMcpServers);
        Assert.Equal("fast", recycledFlavor.Model);
        Assert.Equal("none", recycledFlavor.ReasoningEffort);
        Assert.Equal(2, oldSetCount);
        Assert.Equal(2, freshSetCount);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    private AcpSessionManager NewManager(
        Func<AcpClient> acquire,
        Func<AcpFlavor, AcpClient>? recycle = null)
    {
        var yolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
        return new AcpSessionManager(
            (_, _) => Task.FromResult(acquire()),
            (flavor, _, _) => Task.FromResult<AcpClient?>(recycle?.Invoke(flavor) ?? acquire()),
            yolo,
            NullLogger<AcpSessionManager>.Instance,
            _root);
    }

    private AcpSessionManager NewManager(
        Func<AcpClient> acquire,
        Func<AcpFlavor, CancellationToken, Task<AcpClient>> recycle)
    {
        var yolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
        return new AcpSessionManager(
            (_, _) => Task.FromResult(acquire()),
            async (flavor, _, ct) => await recycle(flavor, ct),
            yolo,
            NullLogger<AcpSessionManager>.Instance,
            _root);
    }

    private AcpSessionManager NewManager(
        Func<AcpClient> acquire,
        Func<AcpFlavor, AcpClient, CancellationToken, Task<AcpClient?>> recycle)
    {
        var yolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
        return new AcpSessionManager(
            (_, _) => Task.FromResult(acquire()),
            recycle,
            yolo,
            NullLogger<AcpSessionManager>.Instance,
            _root);
    }

    private SessionRegistry NewRegistry(AcpSessionManager manager)
        => NewRegistry(manager, NewHostOwnership());

    private HostOwnership NewHostOwnership()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:HostOwnershipFile"] = Path.Combine(_root, "hostownership.json"),
            })
            .Build();
        return new HostOwnership(NullLogger<HostOwnership>.Instance, config);
    }

    private SessionRegistry NewRegistry(
        AcpSessionManager manager,
        HostOwnership hostOwnership)
    {
        Directory.CreateDirectory(_root);
        var scanner = new SessionScanner(NullLogger<SessionScanner>.Instance, _root);
        var yolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
        return new SessionRegistry(
            manager,
            scanner,
            hostOwnership,
            yolo,
            NullLogger<SessionRegistry>.Instance);
    }

    private void CreateSessionLock(string sessionId, int pid)
    {
        var directory = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(LockPath(sessionId, pid), "");
    }

    private string LockPath(string sessionId, int pid) =>
        Path.Combine(_root, sessionId, $"inuse.{pid}.lock");

    private static JsonObject ConfigState(string sessionId, params JsonObject[] configOptions) =>
        new()
        {
            ["sessionId"] = sessionId,
            ["configOptions"] = new JsonArray(configOptions.Cast<JsonNode?>().ToArray()),
        };

    private static JsonObject SelectOption(
        string id,
        string name,
        string category,
        string currentValue,
        params (string Value, string Name)[] values) =>
        new()
        {
            ["id"] = id,
            ["name"] = name,
            ["category"] = category,
            ["type"] = "select",
            ["currentValue"] = currentValue,
            ["options"] = new JsonArray(values.Select(value => (JsonNode?)new JsonObject
            {
                ["value"] = value.Value,
                ["name"] = value.Name,
            }).ToArray()),
        };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed class FakeAcpClient(
        int? processId,
        Func<string, JsonObject?, CancellationToken, int, Task<JsonNode?>> call,
        Func<ValueTask>? dispose = null,
        bool publishesOrderedConfigState = false,
        Func<string, JsonObject, Task>? notify = null)
        : AcpClient(NullLogger<AcpClient>.Instance)
    {
        public override int? ProcessId => processId;
        public override bool PublishesOrderedConfigState => publishesOrderedConfigState;

        public override Task<JsonNode?> CallAsync(
            string method,
            JsonObject? @params,
            CancellationToken ct,
            int timeoutSec = 120) =>
            call(method, @params, ct, timeoutSec);

        public override Task NotifyAsync(string method, JsonObject @params) =>
            notify?.Invoke(method, @params) ?? Task.CompletedTask;

        public override ValueTask DisposeAsync() =>
            dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}
