using System.Collections.Concurrent;
using System.Threading.Channels;
using Magpilot.Agent.Acp;
using Magpilot.Agent.Runtime.Sdk;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Runtime;

/// <summary>
/// Routes each session to ACP or the SDK backend while both implementations
/// coexist during the migration.
/// </summary>
internal sealed class SessionRuntimeRouter : IAgentSessionRuntime
{
    private readonly IAgentSessionRuntime _acp;
    private readonly IAgentSessionRuntime _sdk;
    private readonly SessionRuntimeBackendOptions _options;
    private readonly ConcurrentDictionary<string, SessionRuntimeBackend> _routes = new();

    public SessionRuntimeRouter(
        AcpSessionManager acp,
        SdkSessionRuntime sdk,
        SessionRuntimeBackendOptions options)
        : this((IAgentSessionRuntime)acp, sdk, options)
    {
    }

    internal SessionRuntimeRouter(
        IAgentSessionRuntime acp,
        IAgentSessionRuntime sdk,
        SessionRuntimeBackendOptions options)
    {
        _acp = acp;
        _sdk = sdk;
        _options = options;
    }

    public bool IsQuarantined(string sessionId) =>
        RuntimeForSession(sessionId).IsQuarantined(sessionId);

    public bool IsAttached(string sessionId) =>
        _sdk.IsAttached(sessionId) || _acp.IsAttached(sessionId);

    public bool IsResident(string sessionId) =>
        _sdk.IsResident(sessionId) || _acp.IsResident(sessionId);

    public SessionRuntimeProfile? EffectiveProfile(string sessionId)
    {
        var runtime = RuntimeForSession(sessionId);
        return runtime.EffectiveProfile(sessionId)
            ?? Other(runtime).EffectiveProfile(sessionId);
    }

    public SessionRuntimeStatus? RuntimeStatus(string sessionId)
    {
        var runtime = RuntimeForSession(sessionId);
        return runtime.RuntimeStatus(sessionId)
            ?? Other(runtime).RuntimeStatus(sessionId);
    }

    public Task<IReadOnlyList<SessionModelOption>> ListModelOptionsAsync(
        string sessionId,
        CancellationToken ct) =>
        RuntimeForSession(sessionId).ListModelOptionsAsync(sessionId, ct);

    public bool IsTurnInFlight(
        string sessionId,
        out SessionInFlightEntry entry) =>
        RuntimeForSession(sessionId)
            .IsTurnInFlight(sessionId, out entry);

    public Task WaitForTurnBoundaryAsync(
        string sessionId,
        CancellationToken ct) =>
        RuntimeForSession(sessionId)
            .WaitForTurnBoundaryAsync(sessionId, ct);

    public Task BeginSessionDrainAsync(
        string sessionId,
        CancellationToken ct) =>
        RuntimeForSession(sessionId)
            .BeginSessionDrainAsync(sessionId, ct);

    public Task EndSessionDrainAsync(string sessionId) =>
        RuntimeForSession(sessionId)
            .EndSessionDrainAsync(sessionId);

    public async Task<string> NewSessionAsync(
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached = null)
    {
        var runtime = RuntimeFor(profile);
        var sessionId = await runtime.NewSessionAsync(
            cwd,
            profile,
            ct,
            onAttached);
        _routes[sessionId] = profile.Backend;
        return sessionId;
    }

    public async Task ReloadFromDiskAsync(
        string sessionId,
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached = null)
    {
        var runtime = RuntimeFor(profile);
        await runtime.ReloadFromDiskAsync(
            sessionId,
            cwd,
            profile,
            ct,
            onAttached);
        _routes[sessionId] = profile.Backend;
    }

    public Task ApplyOwnedConfigurationAsync(
        string sessionId,
        SessionRuntimeProfile requested,
        bool processScopeSpecified,
        CancellationToken ct)
    {
        var runtime = RuntimeForSession(sessionId);
        if (!ReferenceEquals(runtime, RuntimeFor(requested)))
        {
            throw new SessionRuntimeConfigurationException(
                $"Session {sessionId} cannot switch runtime backend while attached.");
        }
        return runtime.ApplyOwnedConfigurationAsync(
            sessionId,
            requested,
            processScopeSpecified,
            ct);
    }

    public bool MayBeStale(string sessionId) =>
        RuntimeForSession(sessionId).MayBeStale(sessionId);

    public void ResyncWatermark(string sessionId) =>
        RuntimeForSession(sessionId).ResyncWatermark(sessionId);

    public Task<SessionRecycleOutcome> RecycleForStaleAsync(
        string sessionId,
        Func<string, string?> cwdResolver,
        CancellationToken ct) =>
        RuntimeForSession(sessionId)
            .RecycleForStaleAsync(sessionId, cwdResolver, ct);

    public bool HasForeignLiveHolder(string sessionId) =>
        RuntimeForSession(sessionId).HasForeignLiveHolder(sessionId);

    public IReadOnlyList<int> ForeignLiveHolderPids(string sessionId) =>
        RuntimeForSession(sessionId).ForeignLiveHolderPids(sessionId);

    public IReadOnlyList<int> EvictForeignLiveHolders(string sessionId) =>
        RuntimeForSession(sessionId).EvictForeignLiveHolders(sessionId);

    public Task<Task> StartPromptAsync(
        string sessionId,
        string text,
        CancellationToken reservationCt,
        CancellationToken promptCt,
        string? requester = null,
        string? source = null) =>
        RuntimeForSession(sessionId).StartPromptAsync(
            sessionId,
            text,
            reservationCt,
            promptCt,
            requester,
            source);

    public Task PromptAsync(
        string sessionId,
        string text,
        CancellationToken ct,
        string? requester = null,
        string? source = null) =>
        RuntimeForSession(sessionId).PromptAsync(
            sessionId,
            text,
            ct,
            requester,
            source);

    public void PublishToSubscribers(string sessionId, StreamEvent evt) =>
        RuntimeForSession(sessionId)
            .PublishToSubscribers(sessionId, evt);

    public Task CancelAsync(string sessionId, CancellationToken ct) =>
        RuntimeForSession(sessionId).CancelAsync(sessionId, ct);

    public Task<SessionRuntimeProfile?> CloseAsync(
        string sessionId,
        string? sessionsRoot,
        CancellationToken ct) =>
        RuntimeForSession(sessionId)
            .CloseAsync(sessionId, sessionsRoot, ct);

    public Task<SessionRuntimeProfile?> ForceDetachAsync(
        string sessionId,
        CancellationToken ct) =>
        RuntimeForSession(sessionId)
            .ForceDetachAsync(sessionId, ct);

    public ChannelReader<StreamEvent> Subscribe(string sessionId) =>
        RuntimeForSession(sessionId).Subscribe(sessionId);

    public void Unsubscribe(
        string sessionId,
        ChannelReader<StreamEvent> reader) =>
        RuntimeForSession(sessionId).Unsubscribe(sessionId, reader);

    public bool ResolveApproval(string approvalId, string optionId) =>
        _sdk.ResolveApproval(approvalId, optionId)
        || _acp.ResolveApproval(approvalId, optionId);

    public async Task<int> SweepStalledTurnsAsync(
        TimeSpan threshold,
        Func<string, string?> cwdResolver,
        CancellationToken ct)
    {
        var acpRecovered = await _acp.SweepStalledTurnsAsync(
            threshold,
            cwdResolver,
            ct);
        var sdkRecovered = await _sdk.SweepStalledTurnsAsync(
            threshold,
            cwdResolver,
            ct);
        return acpRecovered + sdkRecovered;
    }

    private IAgentSessionRuntime RuntimeFor(SessionRuntimeProfile profile) =>
        profile.Backend switch
        {
            SessionRuntimeBackend.Acp => _acp,
            SessionRuntimeBackend.Sdk => _sdk,
            _ => throw new InvalidOperationException(
                $"Unknown session runtime backend {profile.Backend}."),
        };

    private IAgentSessionRuntime RuntimeForSession(string sessionId)
    {
        if (_routes.TryGetValue(sessionId, out var backend))
            return backend == SessionRuntimeBackend.Sdk ? _sdk : _acp;
        if (_sdk.IsAttached(sessionId) || _sdk.IsResident(sessionId))
            return _sdk;
        if (_acp.IsAttached(sessionId) || _acp.IsResident(sessionId))
            return _acp;
        return _options.DefaultBackend == SessionRuntimeBackend.Sdk
            ? _sdk
            : _acp;
    }

    private IAgentSessionRuntime Other(IAgentSessionRuntime runtime) =>
        ReferenceEquals(runtime, _sdk) ? _acp : _sdk;
}
