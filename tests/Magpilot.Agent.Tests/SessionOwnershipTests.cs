using System.Threading.Channels;
using Magpilot.Agent.Api;
using Magpilot.Agent.Runtime;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SessionOwnershipTests : IDisposable
{
    private const int DeadPid = 2147483646;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"magpilot-ownership-{Guid.NewGuid():N}");

    public SessionOwnershipTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void State_projection_uses_every_live_lock()
    {
        const string sessionId = "multi-lock";
        WriteSession(sessionId);
        WriteLock(sessionId, DeadPid);
        WriteLock(sessionId, Environment.ProcessId);
        var registry = CreateRegistry(new StubRuntime());

        var state = registry.GetState(sessionId);

        Assert.NotNull(state);
        Assert.Equal(SessionOwner.External, state.Owner);
        Assert.Equal([Environment.ProcessId], state.ForeignHolderPids);
        Assert.Equal(SessionState.Locked, state.Info.State);
        Assert.Equal(Environment.ProcessId, state.Info.OwnerPid);
    }

    [Fact]
    public async Task Runtime_plus_foreign_holder_is_contended()
    {
        var runtime = new StubRuntime
        {
            NewSessionId = "contended",
            ForeignPids = [Environment.ProcessId],
            WriteRuntimeLock = true,
        };
        var registry = CreateRegistry(runtime);
        var session = await registry.CreateAsync(
            _root,
            CancellationToken.None);

        var state = registry.GetState(session.Id);

        Assert.NotNull(state);
        Assert.Equal(SessionOwner.Contended, state.Owner);
        Assert.Equal([Environment.ProcessId], state.ForeignHolderPids);
        Assert.Equal(SessionState.Locked, state.Info.State);
    }

    [Fact]
    public void Contended_state_blocks_prompt_admission()
    {
        var state = new SessionStateInfo(
            new SessionInfo(
                "contended-prompt",
                SessionState.Locked,
                null,
                null,
                null,
                null,
                Environment.ProcessId,
                null,
                null),
            SessionOwner.Contended,
            null,
            null,
            SessionActivity.Idle,
            null,
            null,
            null,
            [Environment.ProcessId]);

        var conflict = AgentEndpoints.PromptOwnershipConflict(state);

        Assert.NotNull(conflict);
        Assert.True(conflict.NeedsRelease);
        Assert.Equal(Environment.ProcessId, conflict.HostPid);
    }

    [Fact]
    public async Task Contended_runtime_rejects_polite_terminal_acquire()
    {
        var runtime = new StubRuntime
        {
            NewSessionId = "contended-acquire",
            ForeignPids = [Environment.ProcessId],
            WriteRuntimeLock = true,
        };
        var registry = CreateRegistry(runtime);
        var session = await registry.CreateAsync(
            _root,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AcquireForHostAsync(
                session.Id,
                Environment.ProcessId + 1,
                force: false,
                CancellationToken.None));
        Assert.Empty(runtime.EvictedPids);
    }

    [Fact]
    public async Task Force_adopt_evicts_the_complete_foreign_set()
    {
        const string sessionId = "force-adopt";
        WriteSession(sessionId);
        WriteLock(sessionId, Environment.ProcessId);
        var runtime = new StubRuntime
        {
            ForeignPids = [Environment.ProcessId, DeadPid],
        };
        var registry = CreateRegistry(runtime);

        var session = await registry.AdoptAsync(
            sessionId,
            force: true,
            CancellationToken.None);

        Assert.Equal([Environment.ProcessId, DeadPid], runtime.EvictedPids);
        Assert.Equal(SessionState.Owned, session.State);
        Assert.Equal(SessionOwner.Agent, registry.GetState(sessionId)!.Owner);
    }

    [Fact]
    public async Task Direct_agent_takeover_reclaims_a_terminal_lease()
    {
        var runtime = new StubRuntime
        {
            NewSessionId = "terminal-takeover",
            WriteRuntimeLock = true,
        };
        var registry = CreateRegistry(runtime);
        var session = await registry.CreateAsync(
            _root,
            CancellationToken.None);
        var acquired = await registry.AcquireForHostAsync(
            session.Id,
            Environment.ProcessId,
            force: false,
            CancellationToken.None);
        Assert.NotNull(acquired.HostLeaseId);
        runtime.ForeignPids = [Environment.ProcessId];

        var taken = await registry.TakeOverForAgentAsync(
            session.Id,
            force: true,
            CancellationToken.None);

        Assert.Equal(SessionOwner.Agent, taken.Owner);
        Assert.Null(taken.HostLeaseId);
        Assert.Equal([Environment.ProcessId], runtime.EvictedPids);
    }

    [Fact]
    public async Task Profileless_terminal_handback_uses_configured_default_backend()
    {
        const string sessionId = "default-backend-handback";
        WriteSession(sessionId);
        var runtime = new StubRuntime();
        var registry = CreateRegistry(runtime);
        var acquired = await registry.AcquireForHostAsync(
            sessionId,
            Environment.ProcessId,
            force: false,
            CancellationToken.None);

        var released = await registry.ReleaseFromHostAsync(
            sessionId,
            acquired.HostLeaseId!.Value,
            CancellationToken.None);

        Assert.Equal(SessionOwner.Agent, released.Owner);
        Assert.Equal(SessionRuntimeBackend.Sdk, runtime.LastProfile!.Backend);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private SessionRegistry CreateRegistry(StubRuntime runtime)
    {
        var ownershipPath = Path.Combine(_root, "hostownership.json");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:HostOwnershipFile"] = ownershipPath,
            })
            .Build();
        return new SessionRegistry(
            runtime,
            new SessionScanner(
                NullLogger<SessionScanner>.Instance,
                _root),
            new HostOwnership(
                NullLogger<HostOwnership>.Instance,
                config),
            new YoloRegistry(NullLogger<YoloRegistry>.Instance),
            NullLogger<SessionRegistry>.Instance,
            SessionRuntimeBackendOptions.SdkDefault);
    }

    private void WriteSession(string sessionId)
    {
        var directory = Directory.CreateDirectory(
            Path.Combine(_root, sessionId));
        File.WriteAllText(
            Path.Combine(directory.FullName, "workspace.yaml"),
            $"cwd: \"{_root}\"{Environment.NewLine}");
        File.WriteAllText(
            Path.Combine(directory.FullName, "events.jsonl"),
            "{}\n");
    }

    private void WriteLock(string sessionId, int pid) =>
        File.WriteAllText(
            Path.Combine(_root, sessionId, $"inuse.{pid}.lock"),
            "");

    private sealed class StubRuntime : IAgentSessionRuntime
    {
        private readonly Channel<StreamEvent> _events =
            Channel.CreateUnbounded<StreamEvent>();

        public string NewSessionId { get; init; } = "session";
        public bool WriteRuntimeLock { get; init; }
        public int[] ForeignPids { get; set; } = [];
        public int[] EvictedPids { get; private set; } = [];
        public SessionRuntimeProfile? LastProfile { get; private set; }
        private bool Attached { get; set; }
        private string? Root { get; set; }

        public bool IsQuarantined(string sessionId) => false;
        public bool IsAttached(string sessionId) => Attached;
        public bool IsResident(string sessionId) => Attached;
        public SessionRuntimeProfile? EffectiveProfile(string sessionId) => LastProfile;
        public SessionRuntimeStatus? RuntimeStatus(string sessionId) => null;
        public Task<IReadOnlyList<SessionModelOption>> ListModelOptionsAsync(
            string sessionId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionModelOption>>([]);
        public bool IsTurnInFlight(string sessionId, out SessionInFlightEntry entry)
        {
            entry = default;
            return false;
        }
        public Task WaitForTurnBoundaryAsync(string sessionId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task BeginSessionDrainAsync(string sessionId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task EndSessionDrainAsync(string sessionId) => Task.CompletedTask;

        public Task<string> NewSessionAsync(
            string cwd,
            SessionRuntimeProfile profile,
            CancellationToken ct,
            Action<string>? onAttached = null)
        {
            Root = cwd;
            LastProfile = profile;
            Attached = true;
            WriteSession(cwd, NewSessionId);
            if (WriteRuntimeLock)
                WriteLock(cwd, NewSessionId, Environment.ProcessId);
            onAttached?.Invoke(NewSessionId);
            return Task.FromResult(NewSessionId);
        }

        public Task ReloadFromDiskAsync(
            string sessionId,
            string cwd,
            SessionRuntimeProfile profile,
            CancellationToken ct,
            Action<string>? onAttached = null)
        {
            Root = cwd;
            LastProfile = profile;
            Attached = true;
            onAttached?.Invoke(sessionId);
            return Task.CompletedTask;
        }

        public Task ApplyOwnedConfigurationAsync(
            string sessionId,
            SessionRuntimeProfile requested,
            bool processScopeSpecified,
            CancellationToken ct)
        {
            LastProfile = requested;
            return Task.CompletedTask;
        }

        public bool MayBeStale(string sessionId) => false;
        public void ResyncWatermark(string sessionId) { }
        public Task<SessionRecycleOutcome> RecycleForStaleAsync(
            string sessionId,
            Func<string, string?> cwdResolver,
            CancellationToken ct) =>
            Task.FromResult(SessionRecycleOutcome.NotLoaded);
        public bool HasForeignLiveHolder(string sessionId) => ForeignPids.Length > 0;
        public IReadOnlyList<int> ForeignLiveHolderPids(string sessionId) => ForeignPids;
        public IReadOnlyList<int> EvictForeignLiveHolders(string sessionId)
        {
            EvictedPids = ForeignPids;
            ForeignPids = [];
            return EvictedPids;
        }
        public Task<Task> StartPromptAsync(
            string sessionId,
            string text,
            CancellationToken reservationCt,
            CancellationToken promptCt,
            string? requester = null,
            string? source = null) =>
            Task.FromResult(Task.CompletedTask);
        public Task PromptAsync(
            string sessionId,
            string text,
            CancellationToken ct,
            string? requester = null,
            string? source = null) =>
            Task.CompletedTask;
        public void PublishToSubscribers(string sessionId, StreamEvent evt) { }
        public Task CancelAsync(string sessionId, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<SessionRuntimeProfile?> CloseAsync(
            string sessionId,
            string? sessionsRoot,
            CancellationToken ct)
        {
            Attached = false;
            return Task.FromResult(LastProfile);
        }
        public Task<SessionRuntimeProfile?> ForceDetachAsync(
            string sessionId,
            CancellationToken ct)
        {
            Attached = false;
            return Task.FromResult(LastProfile);
        }
        public ChannelReader<StreamEvent> Subscribe(string sessionId) =>
            _events.Reader;
        public void Unsubscribe(
            string sessionId,
            ChannelReader<StreamEvent> reader) { }
        public bool ResolveApproval(string approvalId, string optionId) => false;
        public Task<int> SweepStalledTurnsAsync(
            TimeSpan threshold,
            Func<string, string?> cwdResolver,
            CancellationToken ct) =>
            Task.FromResult(0);

        private static void WriteSession(string root, string sessionId)
        {
            var directory = Directory.CreateDirectory(
                Path.Combine(root, sessionId));
            File.WriteAllText(
                Path.Combine(directory.FullName, "workspace.yaml"),
                $"cwd: \"{root}\"{Environment.NewLine}");
            File.WriteAllText(
                Path.Combine(directory.FullName, "events.jsonl"),
                "{}\n");
        }

        private static void WriteLock(string root, string sessionId, int pid) =>
            File.WriteAllText(
                Path.Combine(root, sessionId, $"inuse.{pid}.lock"),
                "");
    }
}
