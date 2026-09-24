using System.Threading.Channels;
using Magpilot.Agent.Runtime;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SessionRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"magpilot-runtime-{Guid.NewGuid():N}");

    public SessionRuntimeTests() => Directory.CreateDirectory(_root);

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
        var registry = CreateRegistry(runtime);
        var session = await registry.CreateAsync(
            _root,

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
        var registry = CreateRegistry(runtime);
        var session = await registry.CreateAsync(
            _root,

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

    private SessionRegistry CreateRegistry(IAgentSessionRuntime runtime) =>
        new(
            runtime,
            new SessionScanner(
                NullLogger<SessionScanner>.Instance,
                _root),
            new HostOwnership(
                NullLogger<HostOwnership>.Instance),
            new YoloRegistry(NullLogger<YoloRegistry>.Instance),
            NullLogger<SessionRegistry>.Instance);

    private sealed class RecordingRuntime(
        string sessionId = "session-1")
        : IAgentSessionRuntime
    {
        private readonly Channel<StreamEvent> _channel =
            Channel.CreateUnbounded<StreamEvent>();

        public SessionRuntimeProfile? LastProfile { get; private set; }
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
                    LastProfile.Model,
                    LastProfile.Model,
                    LastProfile.ReasoningEffort,
                    null,
                    null,
                    null,
                    ModelOptions.Count > 0,
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
        public bool HasForeignLiveHolder(string id) => false;
        public IReadOnlyList<int> ForeignLiveHolderPids(string id) => [];
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
        public Task CancelAsync(string id, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<SessionRuntimeProfile?> CloseAsync(
            string id,
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
            CancellationToken ct) =>
            Task.FromResult(0);
    }
}
