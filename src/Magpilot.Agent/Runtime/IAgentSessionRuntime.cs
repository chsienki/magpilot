using System.Threading.Channels;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Runtime;

public enum SessionRecycleOutcome
{
    NotLoaded,
    Busy,
    Recycled,
}

public readonly record struct SessionInFlightEntry(
    string? Requester,
    DateTimeOffset StartedAt);

/// <summary>
/// Runtime-independent session operations consumed by the agent HTTP surface,
/// registry, handoff coordinator, and turn watchdog.
/// </summary>
public interface IAgentSessionRuntime
{
    bool IsQuarantined(string sessionId);
    bool IsAttached(string sessionId);
    bool IsResident(string sessionId);
    SessionRuntimeProfile? EffectiveProfile(string sessionId);
    SessionRuntimeStatus? RuntimeStatus(string sessionId);
    Task<IReadOnlyList<SessionModelOption>> ListModelOptionsAsync(
        string sessionId,
        CancellationToken ct);
    bool IsTurnInFlight(string sessionId, out SessionInFlightEntry entry);
    Task WaitForTurnBoundaryAsync(string sessionId, CancellationToken ct);
    Task BeginSessionDrainAsync(string sessionId, CancellationToken ct);
    Task EndSessionDrainAsync(string sessionId);

    Task<string> NewSessionAsync(
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached = null);

    Task ReloadFromDiskAsync(
        string sessionId,
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached = null);

    Task ApplyOwnedConfigurationAsync(
        string sessionId,
        SessionRuntimeProfile requested,
        bool processScopeSpecified,
        CancellationToken ct);

    bool MayBeStale(string sessionId);
    void ResyncWatermark(string sessionId);
    Task<SessionRecycleOutcome> RecycleForStaleAsync(
        string sessionId,
        Func<string, string?> cwdResolver,
        CancellationToken ct);

    bool HasForeignLiveHolder(string sessionId);
    IReadOnlyList<int> EvictForeignLiveHolders(string sessionId);

    Task<Task> StartPromptAsync(
        string sessionId,
        string text,
        CancellationToken reservationCt,
        CancellationToken promptCt,
        string? requester = null,
        string? source = null);

    Task PromptAsync(
        string sessionId,
        string text,
        CancellationToken ct,
        string? requester = null,
        string? source = null);

    void PublishToSubscribers(string sessionId, StreamEvent evt);
    Task CancelAsync(string sessionId, CancellationToken ct);
    Task<SessionRuntimeProfile?> CloseAsync(
        string sessionId,
        string? sessionsRoot,
        CancellationToken ct);
    Task<SessionRuntimeProfile?> ForceDetachAsync(string sessionId, CancellationToken ct);

    ChannelReader<StreamEvent> Subscribe(string sessionId);
    void Unsubscribe(string sessionId, ChannelReader<StreamEvent> reader);
    bool ResolveApproval(string approvalId, string optionId);

    Task<int> SweepStalledTurnsAsync(
        TimeSpan threshold,
        Func<string, string?> cwdResolver,
        CancellationToken ct);
}
