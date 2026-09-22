using System.Threading.Channels;
using Magpilot.Agent.Runtime;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SessionRuntimeRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"magpilot-runtime-routing-{Guid.NewGuid():N}");

    public SessionRuntimeRoutingTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(null, SessionRuntimeBackend.Sdk)]
    [InlineData("", SessionRuntimeBackend.Sdk)]
    [InlineData("sdk", SessionRuntimeBackend.Sdk)]
    [InlineData("acp", SessionRuntimeBackend.Acp)]
    public void Runtime_backend_value_selects_expected_default(
        string? value,
        SessionRuntimeBackend expected)
    {
        Assert.Equal(
            expected,
            SessionRuntimeBackendOptions.FromValue(value).DefaultBackend);
    }

    [Fact]
    public void Invalid_runtime_backend_value_fails_explicitly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SessionRuntimeBackendOptions.FromValue("fallback"));

        Assert.Contains("must be 'acp' or 'sdk'", ex.Message);
    }

    [Fact]
    public async Task Registry_selects_the_configured_sdk_backend_for_ordinary_sessions()
    {
        var runtime = new RecordingRuntime();
        var registry = CreateRegistry(
            runtime,
            new SessionRuntimeBackendOptions(SessionRuntimeBackend.Sdk));

        await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None);

        Assert.Equal(SessionRuntimeBackend.Sdk, runtime.LastProfile?.Backend);
    }

    [Fact]
    public async Task Registry_keeps_agency_on_acp_when_sdk_is_the_default()
    {
        var runtime = new RecordingRuntime();
        var registry = CreateRegistry(
            runtime,
            new SessionRuntimeBackendOptions(SessionRuntimeBackend.Sdk));

        await registry.CreateAsync(
            _root,
            useAgency: true,
            CancellationToken.None);

        Assert.Equal(SessionRuntimeBackend.Acp, runtime.LastProfile?.Backend);
    }

    [Fact]
    public async Task Router_keeps_subsequent_operations_on_the_creating_backend()
    {
        var acp = new RecordingRuntime("acp-session");
        var sdk = new RecordingRuntime("sdk-session");
        var router = new SessionRuntimeRouter(
            acp,
            sdk,
            new SessionRuntimeBackendOptions(SessionRuntimeBackend.Sdk));
        var profile = SessionRuntimeProfile.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            backend: SessionRuntimeBackend.Sdk);

        var sessionId = await router.NewSessionAsync(
            _root,
            profile,
            CancellationToken.None);
        await router.CancelAsync(sessionId, CancellationToken.None);

        Assert.Equal(0, acp.CancelCount);
        Assert.Equal(1, sdk.CancelCount);
    }

    [Fact]
    public async Task Router_rejects_an_in_place_backend_switch()
    {
        var acp = new RecordingRuntime("acp-session");
        var sdk = new RecordingRuntime("sdk-session");
        var router = new SessionRuntimeRouter(
            acp,
            sdk,
            new SessionRuntimeBackendOptions(SessionRuntimeBackend.Sdk));
        var sdkProfile = SessionRuntimeProfile.Resolve(
            useAgency: false,
            model: null,
            reasoningEffort: null,
            backend: SessionRuntimeBackend.Sdk);
        var sessionId = await router.NewSessionAsync(
            _root,
            sdkProfile,
            CancellationToken.None);

        await Assert.ThrowsAsync<SessionRuntimeConfigurationException>(() =>
            router.ApplyOwnedConfigurationAsync(
                sessionId,
                sdkProfile with { Backend = SessionRuntimeBackend.Acp },
                processScopeSpecified: false,
                CancellationToken.None));
    }

    [Fact]
    public async Task Registry_updates_idle_sdk_model_and_preserves_supported_reasoning()
    {
        var runtime = new RecordingRuntime
        {
            ModelOptions =
            [
                new SessionModelOption(
                    "new-model",
                    "New Model",
                    ["low", "high"],
                    "low"),
            ],
        };
        var registry = CreateRegistry(
            runtime,
            new SessionRuntimeBackendOptions(SessionRuntimeBackend.Sdk));
        var session = await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "old-model",
            reasoningEffort: "high");

        var state = await registry.UpdateModelAsync(
            session.Id,
            new SessionModelUpdateRequest("new-model"),
            CancellationToken.None);

        Assert.Equal("new-model", runtime.LastProfile!.Model);
        Assert.Equal("high", runtime.LastProfile.ReasoningEffort);
        Assert.Equal("new-model", state.RuntimeStatus!.ModelId);
    }

    [Fact]
    public async Task Registry_rejects_model_changes_while_a_turn_is_active()
    {
        var runtime = new RecordingRuntime
        {
            TurnInFlight = true,
            ModelOptions =
            [
                new SessionModelOption(
                    "new-model",
                    "New Model",
                    ["low"],
                    "low"),
            ],
        };
        var registry = CreateRegistry(
            runtime,
            new SessionRuntimeBackendOptions(SessionRuntimeBackend.Sdk));
        var session = await registry.CreateAsync(
            _root,
            useAgency: false,
            CancellationToken.None,
            model: "old-model",
            reasoningEffort: "low");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.UpdateModelAsync(
                session.Id,
                new SessionModelUpdateRequest("new-model"),
                CancellationToken.None));

        Assert.Contains("turn is in flight", error.Message);
        Assert.Equal("old-model", runtime.LastProfile!.Model);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SessionRegistry CreateRegistry(
        IAgentSessionRuntime runtime,
        SessionRuntimeBackendOptions options) =>
        new(
            runtime,
            new SessionScanner(
                NullLogger<SessionScanner>.Instance,
                _root),
            new HostOwnership(
                NullLogger<HostOwnership>.Instance),
            new YoloRegistry(NullLogger<YoloRegistry>.Instance),
            NullLogger<SessionRegistry>.Instance,
            options);

    private sealed class RecordingRuntime(
        string sessionId = "session-1")
        : IAgentSessionRuntime
    {
        private readonly Channel<StreamEvent> _channel =
            Channel.CreateUnbounded<StreamEvent>();

        public SessionRuntimeProfile? LastProfile { get; private set; }
        public int CancelCount { get; private set; }
        public bool TurnInFlight { get; init; }
        public IReadOnlyList<SessionModelOption> ModelOptions { get; init; } = [];

        public bool IsQuarantined(string id) => false;
        public bool IsAttached(string id) => true;
        public bool IsResident(string id) => true;
        public SessionRuntimeProfile? EffectiveProfile(string id) => LastProfile;
        public SessionRuntimeStatus? RuntimeStatus(string id) =>
            LastProfile is null
                ? null
                : new SessionRuntimeStatus(
                    LastProfile.Backend.ToString().ToLowerInvariant(),
                    LastProfile.Model,
                    LastProfile.Model,
                    LastProfile.ReasoningEffort,
                    null,
                    null,
                    null,
                    LastProfile.Backend == SessionRuntimeBackend.Sdk
                        && ModelOptions.Count > 0,
                    DateTimeOffset.UtcNow);
        public Task<IReadOnlyList<SessionModelOption>> ListModelOptionsAsync(
            string id,
            CancellationToken ct) =>
            Task.FromResult(ModelOptions);
        public bool IsTurnInFlight(string id, out SessionInFlightEntry entry)
        {
            entry = TurnInFlight
                ? new SessionInFlightEntry("test", DateTimeOffset.UtcNow)
                : default;
            return TurnInFlight;
        }
        public Task WaitForTurnBoundaryAsync(string id, CancellationToken ct) =>
            Task.CompletedTask;
        public Task BeginSessionDrainAsync(string id, CancellationToken ct) =>
            Task.CompletedTask;
        public Task EndSessionDrainAsync(string id) => Task.CompletedTask;
        public Task<string> NewSessionAsync(
            string cwd,
            SessionRuntimeProfile profile,
            CancellationToken ct,
            Action<string>? onAttached = null)
        {
            LastProfile = profile;
            var directory = Directory.CreateDirectory(
                Path.Combine(cwd, sessionId));
            File.WriteAllText(
                Path.Combine(directory.FullName, "workspace.yaml"),
                $"cwd: {cwd}{Environment.NewLine}");
            onAttached?.Invoke(sessionId);
            return Task.FromResult(sessionId);
        }
        public Task ReloadFromDiskAsync(
            string id,
            string cwd,
            SessionRuntimeProfile profile,
            CancellationToken ct,
            Action<string>? onAttached = null)
        {
            LastProfile = profile;
            onAttached?.Invoke(id);
            return Task.CompletedTask;
        }
        public Task ApplyOwnedConfigurationAsync(
            string id,
            SessionRuntimeProfile requested,
            bool processScopeSpecified,
            CancellationToken ct)
        {
            LastProfile = requested;
            return Task.CompletedTask;
        }
        public bool MayBeStale(string id) => false;
        public void ResyncWatermark(string id) { }
        public Task<SessionRecycleOutcome> RecycleForStaleAsync(
            string id,
            Func<string, string?> cwdResolver,
            CancellationToken ct) =>
            Task.FromResult(SessionRecycleOutcome.NotLoaded);
        public bool HasForeignLiveHolder(string id) => false;
        public IReadOnlyList<int> EvictForeignLiveHolders(string id) => [];
        public Task<Task> StartPromptAsync(
            string id,
            string text,
            CancellationToken reservationCt,
            CancellationToken promptCt,
            string? requester = null,
            string? source = null) =>
            Task.FromResult(Task.CompletedTask);
        public Task PromptAsync(
            string id,
            string text,
            CancellationToken ct,
            string? requester = null,
            string? source = null) =>
            Task.CompletedTask;
        public void PublishToSubscribers(string id, StreamEvent evt) { }
        public Task CancelAsync(string id, CancellationToken ct)
        {
            CancelCount++;
            return Task.CompletedTask;
        }
        public Task<SessionRuntimeProfile?> CloseAsync(
            string id,
            string? sessionsRoot,
            CancellationToken ct) =>
            Task.FromResult(LastProfile);
        public Task<SessionRuntimeProfile?> ForceDetachAsync(
            string id,
            CancellationToken ct) =>
            Task.FromResult(LastProfile);
        public ChannelReader<StreamEvent> Subscribe(string id) =>
            _channel.Reader;
        public void Unsubscribe(string id, ChannelReader<StreamEvent> reader) { }
        public bool ResolveApproval(string approvalId, string optionId) => false;
        public Task<int> SweepStalledTurnsAsync(
            TimeSpan threshold,
            Func<string, string?> cwdResolver,
            CancellationToken ct) =>
            Task.FromResult(0);
    }
}
