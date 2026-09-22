using System.Threading.Channels;
using Magpilot.Agent.Runtime;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Acp;

public sealed partial class AcpSessionManager
{
    SessionRuntimeProfile? IAgentSessionRuntime.EffectiveProfile(string sessionId) =>
        EffectiveFlavor(sessionId)?.ToRuntimeProfile();

    SessionRuntimeStatus? IAgentSessionRuntime.RuntimeStatus(string sessionId)
    {
        var profile = EffectiveFlavor(sessionId)?.ToRuntimeProfile();
        return profile is null
            ? null
            : new SessionRuntimeStatus(
                "acp",
                profile.Model,
                profile.Model,
                profile.ReasoningEffort,
                CurrentTokens: null,
                TokenLimit: null,
                AiCreditsUsed: null,
                CanEditModel: false,
                DateTimeOffset.UtcNow);
    }

    Task<IReadOnlyList<SessionModelOption>>
        IAgentSessionRuntime.ListModelOptionsAsync(
            string sessionId,
            CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionModelOption>>([]);

    bool IAgentSessionRuntime.IsTurnInFlight(
        string sessionId,
        out SessionInFlightEntry entry)
    {
        if (IsTurnInFlight(sessionId, out var acpEntry))
        {
            entry = new SessionInFlightEntry(acpEntry.Requester, acpEntry.StartedAt);
            return true;
        }

        entry = default;
        return false;
    }

    Task IAgentSessionRuntime.BeginSessionDrainAsync(
        string sessionId,
        CancellationToken ct) =>
        BeginSessionDrainAsync(sessionId, ct);

    Task IAgentSessionRuntime.EndSessionDrainAsync(string sessionId) =>
        EndSessionDrainAsync(sessionId);

    Task<string> IAgentSessionRuntime.NewSessionAsync(
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached) =>
        NewSessionAsync(cwd, AcpFlavor.FromRuntimeProfile(profile), ct, onAttached);

    Task IAgentSessionRuntime.ReloadFromDiskAsync(
        string sessionId,
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached) =>
        ReloadFromDiskAsync(
            sessionId,
            cwd,
            AcpFlavor.FromRuntimeProfile(profile),
            ct,
            onAttached);

    Task IAgentSessionRuntime.ApplyOwnedConfigurationAsync(
        string sessionId,
        SessionRuntimeProfile requested,
        bool processScopeSpecified,
        CancellationToken ct) =>
        ApplyOwnedConfigurationAsync(
            sessionId,
            AcpFlavor.FromRuntimeProfile(requested),
            processScopeSpecified,
            ct);

    async Task<SessionRecycleOutcome> IAgentSessionRuntime.RecycleForStaleAsync(
        string sessionId,
        Func<string, string?> cwdResolver,
        CancellationToken ct) =>
        await RecycleForStaleAsync(sessionId, cwdResolver, ct) switch
        {
            RecycleOutcome.NotLoaded => SessionRecycleOutcome.NotLoaded,
            RecycleOutcome.Busy => SessionRecycleOutcome.Busy,
            RecycleOutcome.Recycled => SessionRecycleOutcome.Recycled,
            _ => throw new InvalidOperationException("Unknown ACP recycle outcome."),
        };

    async Task<SessionRuntimeProfile?> IAgentSessionRuntime.CloseAsync(
        string sessionId,
        string? sessionsRoot,
        CancellationToken ct) =>
        (await CloseAsync(sessionId, sessionsRoot, ct))?.ToRuntimeProfile();

    async Task<SessionRuntimeProfile?> IAgentSessionRuntime.ForceDetachAsync(
        string sessionId,
        CancellationToken ct) =>
        (await ForceDetachAsync(sessionId, ct))?.ToRuntimeProfile();

    bool IAgentSessionRuntime.IsQuarantined(string sessionId) =>
        IsQuarantined(sessionId);

    bool IAgentSessionRuntime.IsAttached(string sessionId) =>
        IsAttached(sessionId);

    bool IAgentSessionRuntime.IsResident(string sessionId) =>
        IsResident(sessionId);

    Task IAgentSessionRuntime.WaitForTurnBoundaryAsync(
        string sessionId,
        CancellationToken ct) =>
        WaitForTurnBoundaryAsync(sessionId, ct);

    bool IAgentSessionRuntime.MayBeStale(string sessionId) =>
        MayBeStale(sessionId);

    void IAgentSessionRuntime.ResyncWatermark(string sessionId) =>
        ResyncWatermark(sessionId);

    bool IAgentSessionRuntime.HasForeignLiveHolder(string sessionId) =>
        HasForeignLiveHolder(sessionId);

    IReadOnlyList<int> IAgentSessionRuntime.ForeignLiveHolderPids(string sessionId) =>
        ForeignLiveHolderPids(sessionId);

    IReadOnlyList<int> IAgentSessionRuntime.EvictForeignLiveHolders(string sessionId) =>
        EvictForeignLiveHolders(sessionId);

    Task<Task> IAgentSessionRuntime.StartPromptAsync(
        string sessionId,
        string text,
        CancellationToken reservationCt,
        CancellationToken promptCt,
        string? requester,
        string? source) =>
        StartPromptAsync(
            sessionId,
            text,
            reservationCt,
            promptCt,
            requester,
            source);

    Task IAgentSessionRuntime.PromptAsync(
        string sessionId,
        string text,
        CancellationToken ct,
        string? requester,
        string? source) =>
        PromptAsync(sessionId, text, ct, requester, source);

    void IAgentSessionRuntime.PublishToSubscribers(string sessionId, StreamEvent evt) =>
        PublishToSubscribers(sessionId, evt);

    Task IAgentSessionRuntime.CancelAsync(string sessionId, CancellationToken ct) =>
        CancelAsync(sessionId, ct);

    ChannelReader<StreamEvent> IAgentSessionRuntime.Subscribe(string sessionId) =>
        Subscribe(sessionId);

    void IAgentSessionRuntime.Unsubscribe(
        string sessionId,
        ChannelReader<StreamEvent> reader) =>
        Unsubscribe(sessionId, reader);

    bool IAgentSessionRuntime.ResolveApproval(string approvalId, string optionId) =>
        ResolveApproval(approvalId, optionId);

    Task<int> IAgentSessionRuntime.SweepStalledTurnsAsync(
        TimeSpan threshold,
        Func<string, string?> cwdResolver,
        CancellationToken ct) =>
        SweepStalledTurnsAsync(threshold, cwdResolver, ct);
}
