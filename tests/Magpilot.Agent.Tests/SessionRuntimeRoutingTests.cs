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

        public bool IsQuarantined(string id) => false;
        public bool IsAttached(string id) => true;
        public bool IsResident(string id) => true;
        public SessionRuntimeProfile? EffectiveProfile(string id) => LastProfile;
        public bool IsTurnInFlight(string id, out SessionInFlightEntry entry)
        {
            entry = default;
            return false;
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
