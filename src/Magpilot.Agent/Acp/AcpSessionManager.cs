using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Acp;

/// <summary>Result of <see cref="AcpSessionManager.RecycleForStaleAsync"/>.</summary>
public enum RecycleOutcome
{
    /// <summary>The session isn't loaded into any child here; nothing to recycle.</summary>
    NotLoaded,
    /// <summary>A co-hosted session has a turn in flight; recycle was refused to avoid killing it.</summary>
    Busy,
    /// <summary>The child was recycled and the session reloaded from disk.</summary>
    Recycled,
}

/// <summary>
/// Higher-level wrapper that bundles ACP method calls with structured event
/// dispatch. Holds a per-session subscriber list so HTTP SSE handlers can
/// fan out updates without re-parsing JSON.
///
/// Sessions are tagged with the <see cref="AcpFlavor"/> they were created
/// against; subsequent prompt/cancel/close calls for that session are routed
/// to the matching <see cref="AcpClient"/> instance from the pool.
/// </summary>
public sealed class AcpSessionManager
{
    private readonly Func<AcpFlavor, CancellationToken, Task<AcpClient>> _acquireClient;
    private readonly Func<AcpFlavor, AcpClient, CancellationToken, Task<AcpClient?>> _recycleClient;
    private readonly Magpilot.Agent.Sessions.YoloRegistry _yolo;
    private readonly ILogger<AcpSessionManager> _logger;
    private readonly Dictionary<string, List<Channel<StreamEvent>>> _subscribers = new();
    private readonly object _subLock = new();
    private readonly Dictionary<string, TaskCompletionSource<ApprovalResponse>> _pendingApprovals = new();
    private readonly object _approvalLock = new();

    /// <summary>
    /// Maps sessionId -> the actual <see cref="AcpClient"/> that owns it.
    /// Multiplexing flavors share one client across sessions; non-multiplexing
    /// flavors (e.g. agency) get a dedicated client per session, also tracked
    /// here so we can clean up on close.
    /// </summary>
    private readonly ConcurrentDictionary<string, AcpClient> _sessionClient = new();

    // Tracks the on-disk events.jsonl size we last synced per session, so a
    // resume can tell whether another process advanced the session past what
    // our in-memory ACP child has seen (the stale-resume condition).
    private readonly Magpilot.Agent.Sessions.SessionFreshness _freshness = new();

    // Per-session set of PIDs of ACP children WE loaded the session into
    // (current + any retired by a reload). A live inuse lock held by a PID not
    // in this set is a genuinely foreign writer -- the signal that a resume may
    // be stale. Without it, our own child's lock (and its post-turn disk flush)
    // would read as staleness and trigger needless reloads.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _ourSessionPids = new();

    private readonly string _sessionStateRoot;

    private string EventsPath(string sessionId) =>
        Path.Combine(_sessionStateRoot, sessionId, "events.jsonl");

    /// <summary>
    /// Per-session "is a turn currently running?" tracking. Set when
    /// <see cref="PromptAsync"/> enters; cleared when it returns. Used by
    /// the GET /api/sessions/{id}/state endpoint to report activity, and
    /// by the magpilot launcher's acquire-for-host flow to politely wait for
    /// a turn boundary before taking ownership.
    /// </summary>
    private readonly ConcurrentDictionary<string, InFlightEntry> _inFlight = new();

    /// <summary>
    /// Per-session signal that fires every time a turn completes or
    /// errors. Used by <see cref="WaitForTurnBoundaryAsync"/> so callers
    /// (e.g. acquire-for-host) can block until the agent is idle without
    /// busy-polling.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _turnDone = new();

    /// <summary>
    /// Per-session timestamp of the last activity (any session/update) from the
    /// child. The turn watchdog compares this against the in-flight start to tell
    /// a live-but-slow turn (still streaming chunks / firing tool calls) apart
    /// from a wedged one -- a hung model request that emits nothing and never
    /// honours session/cancel.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEventAt = new();

    /// <summary>
    /// The flavor each session was loaded/created under, so the watchdog can
    /// recycle the right ACP child (e.g. the phone assistant's fast-model child)
    /// and reload the session on that same flavor rather than downgrading it to
    /// the default.
    /// </summary>
    private readonly ConcurrentDictionary<string, AcpFlavor> _sessionFlavor = new();

    /// <summary>
    /// Latest complete ACP configOptions state per attached session. Both
    /// session setup/set responses and config_option_update notifications replace
    /// this snapshot so a later adopt of an already-Owned session can apply and
    /// verify new requests without calling non-idempotent session/load.
    /// </summary>
    private readonly ConcurrentDictionary<string, SessionConfigSnapshot> _sessionConfig = new();
    private readonly ConcurrentDictionary<string, ExpectedSessionConfig> _expectedConfig = new();
    private readonly object _configStateLock = new();

    /// <summary>
    /// Latest wire-ordered config state observed for a session before its route
    /// was published. session/load can emit a config notification after its
    /// response is dispatched but before the asynchronous caller continuation
    /// attaches routing; retaining the source client here lets attach consume the
    /// truly latest state without accepting late events from retired children.
    /// </summary>
    private readonly ConcurrentDictionary<(AcpClient Client, string SessionId), SessionConfigSnapshot> _pendingConfigState = new();

    /// <summary>
    /// Per-session gate serialising every attach/configure operation for one
    /// session (session/new + config, session/load + config, and in-place
    /// re-pins). Two concurrent adopts of the same session would otherwise
    /// interleave their set_config_option calls -- and their rollbacks -- and
    /// leave the child on a combination neither caller asked for.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();

    /// <summary>
    /// Sessions currently applying/verifying configuration, keyed to the child
    /// generation they are mutating. Shared-child recycle treats these exactly
    /// like in-flight prompts and refuses rather than racing successful
    /// verification with route invalidation.
    /// </summary>
    private readonly ConcurrentDictionary<string, AcpClient> _configuring = new();

    /// <summary>
    /// Short global lease protecting the transition between "route verified and
    /// prompt marked in-flight" and "shared child checked idle and invalidated".
    /// It is never held for a prompt turn, only for the reservation/recycle
    /// boundary, so recycling cannot race a just-accepted prompt.
    /// </summary>
    private readonly SemaphoreSlim _routingGate = new(1, 1);

    /// <summary>
    /// ACP clients for which destructive recycle/disposal has begun. A
    /// session/new or session/load response can arrive after another operation
    /// recycled its child; route publication checks this weak set under
    /// <see cref="_routingGate"/> so that late response can never resurrect a
    /// route to the dead client.
    /// </summary>
    private readonly ConditionalWeakTable<AcpClient, object> _retiredClients = new();

    /// <summary>
    /// Sessions whose routes were atomically invalidated while their old child
    /// is still being disposed/replaced. A concurrent stale-recycle request may
    /// have observed the session just before retirement; retaining the old
    /// generation here lets that request serialize behind the active recycle
    /// and then reload its own session without restoring a route to the retired
    /// client.
    /// </summary>
    private readonly ConcurrentDictionary<string, RetiringSession> _retiringSessions = new();
    private readonly ConditionalWeakTable<AcpClient, SemaphoreSlim> _clientRecycleGates = new();

    /// <summary>
    /// Sessions whose live configuration could not be applied and verified.
    /// Routing is deliberately KEPT (the child really does hold the session, and
    /// copilot has no way to unload it), but the session is refused for prompts
    /// and cancels until a later adopt re-applies and verifies its configuration.
    /// Retaining the route is what makes that retry possible: it goes through the
    /// in-place config path instead of a session/load that would only be told the
    /// session is "already loaded".
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _quarantined = new();

    /// <summary>
    /// Sessions currently draining for an agent-to-host handoff. Prompt
    /// reservation checks this under <see cref="_routingGate"/>, so once handoff
    /// begins no new turn can slip in after the acquire path's idle check.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _draining = new();

    /// <summary>
    /// Sessions still resident in one of our live ACP children even when we no
    /// longer route to them -- copilot implements neither session/close nor a
    /// disk re-read for an already-loaded session, so a detached session stays in
    /// the child's memory until that child dies. Re-attaching such a session has
    /// to recycle the holding child first, otherwise session/load either answers
    /// "already loaded" or silently serves the frozen pre-detach snapshot.
    /// </summary>
    private readonly ConcurrentDictionary<string, AcpClient> _resident = new();

    /// <summary>
    /// Open (pending / in-progress) tool-call ids per session. A turn that is
    /// waiting on a tool it invoked -- a long shell command, a slow MCP call --
    /// is silent on the ACP stream between the tool's <c>tool_call</c> (pending)
    /// and <c>tool_call_update</c> (completed) updates, but it is NOT wedged. The
    /// watchdog consults this so it never mistakes a legitimately long tool for a
    /// hung model. Cleared when the turn ends.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _openToolCalls = new();
    // HandleUpdate is synchronous, so use a small monitor (not the async routing
    // gate) to make "still stalled?" validation and activity updates linearizable.
    // Once recycle validation leaves this lock, the reset decision is committed.
    private readonly object _activityLock = new();

    public AcpSessionManager(AcpFlavorPool pool, Magpilot.Agent.Sessions.YoloRegistry yolo, ILogger<AcpSessionManager> logger)
        : this(pool.AcquireAsync, pool.RecycleAsync, yolo, logger, sessionStateRoot: null)
    {
        pool.OnSessionUpdate += HandleUpdate;
        pool.OnConfigStateObserved += ObserveConfigState;
        pool.OnRequest += HandleRequestAsync;
    }

    internal AcpSessionManager(
        Func<AcpFlavor, CancellationToken, Task<AcpClient>> acquireClient,
        Func<AcpFlavor, AcpClient, CancellationToken, Task<AcpClient?>> recycleClient,
        Magpilot.Agent.Sessions.YoloRegistry yolo,
        ILogger<AcpSessionManager> logger,
        string? sessionStateRoot)
    {
        _acquireClient = acquireClient;
        _recycleClient = recycleClient;
        _yolo = yolo;
        _logger = logger;
        _sessionStateRoot = sessionStateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot",
            "session-state");
    }

    /// <summary>
    /// True if a turn is currently in flight on the agent's side for
    /// the given session.
    /// </summary>
    public bool IsTurnInFlight(string sessionId, out InFlightEntry entry)
        => _inFlight.TryGetValue(sessionId, out entry!);

    /// <summary>
    /// Wait until any in-flight turn for the session reaches a clean
    /// boundary (TurnComplete or error). Returns immediately if no turn
    /// is in flight. Honours <paramref name="ct"/> for the wait; on
    /// cancellation the in-flight turn is NOT aborted -- it'll keep
    /// running, but this caller stops waiting for it.
    /// </summary>
    public async Task WaitForTurnBoundaryAsync(string sessionId, CancellationToken ct)
    {
        while (_inFlight.ContainsKey(sessionId))
        {
            if (_turnDone.TryGetValue(sessionId, out var turnDone))
            {
                await turnDone.Task.WaitAsync(ct);
                return;
            }
            await Task.Yield();
        }
    }

    /// <summary>
    /// Resolve the ACP client owning <paramref name="sessionId"/>. Routing is
    /// authoritative: an unknown session must be adopted/loaded before it can be
    /// prompted. Falling back to the default child would make a failed partial
    /// attach (or a detached session still resident in Copilot memory) promptable
    /// despite the agent no longer owning it. A quarantined session is refused
    /// for the same reason -- its live configuration is not the one we advertise.
    /// </summary>
    private Task<AcpClient> ClientForAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_quarantined.TryGetValue(sessionId, out var reason))
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is quarantined: {reason} Re-adopt it with the intended " +
                "model/reasoning to re-apply and verify its configuration.");
        }
        if (_sessionClient.TryGetValue(sessionId, out var existing))
            return Task.FromResult(existing);

        throw new InvalidOperationException(
            $"Session {sessionId} is not attached to an ACP child; adopt it before sending commands.");
    }

    /// <summary>
    /// True while the session is attached but refused for traffic because its
    /// configuration could not be applied and verified. Exposed so the registry
    /// can report it and so a retry path can tell "attached and usable" from
    /// "attached and awaiting a successful re-configure".
    /// </summary>
    public bool IsQuarantined(string sessionId) => _quarantined.ContainsKey(sessionId);

    /// <summary>
    /// True while the manager still holds ACP routing for the session (whether or
    /// not it is quarantined). Lets the registry tell "we own a live route" from
    /// "our ownership record outlived the route" and re-attach in the latter case.
    /// </summary>
    public bool IsAttached(string sessionId) => _sessionClient.ContainsKey(sessionId);

    /// <summary>
    /// True when one of our live children may still have the session in memory
    /// even though no promptable route is published (detach or indeterminate
    /// session/load). A retry must recycle that child before loading again.
    /// </summary>
    public bool IsResident(string sessionId) => _resident.ContainsKey(sessionId);

    /// <summary>
    /// The flavor a session is currently attached under -- process scope plus the
    /// model/reasoning last confirmed by the child. Used by the handoff path so a
    /// session handed to a launcher and later handed back comes back on the same
    /// child flavor and the same session configuration.
    /// </summary>
    public AcpFlavor? EffectiveFlavor(string sessionId)
    {
        if (!_sessionFlavor.TryGetValue(sessionId, out var flavor))
            return null;
        return _sessionConfig.TryGetValue(sessionId, out var config)
            ? flavor with { Model = config.Model, ReasoningEffort = config.ReasoningEffort }
            : flavor;
    }

    internal async Task BeginSessionDrainAsync(string sessionId, CancellationToken ct)
    {
        await _routingGate.WaitAsync(ct);
        try { _draining[sessionId] = 0; }
        finally { _routingGate.Release(); }
    }

    internal async Task EndSessionDrainAsync(string sessionId)
    {
        await _routingGate.WaitAsync(CancellationToken.None);
        try { _draining.TryRemove(sessionId, out _); }
        finally { _routingGate.Release(); }
    }

    /// <summary>
    /// Take the per-session attach/configure gate. Every mutation of a session's
    /// ACP routing or configuration runs inside it, so concurrent create/adopt
    /// callers queue instead of interleaving RPCs on the same session.
    /// </summary>
    private async Task<SemaphoreSlim> AcquireSessionGateAsync(string sessionId, CancellationToken ct)
    {
        var gate = _sessionGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return gate;
    }

    public async Task<string> NewSessionAsync(
        string cwd,
        AcpFlavor flavor,
        CancellationToken ct,
        Action<string>? onAttached = null)
    {
        var client = await _acquireClient(flavor, ct);
        var res = await client.CallAsync("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, ct);
        var sid = res?["sessionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("session/new returned no sessionId");

        // session/new already mutated the child. Even if the caller cancels at
        // this boundary, finish recording the route so the loaded session is not
        // stranded and can be quarantined/retried safely.
        var gate = await AcquireSessionGateAsync(sid, CancellationToken.None);
        try
        {
            // The child holds the session from here on, so record the route
            // before configuring it -- but quarantined, so nothing can prompt it
            // while its configuration is still unproven. onAttached runs only
            // after the complete requested tuple verifies, so registry ownership
            // never advertises a partially configured session.
            await AttachRoutingAsync(sid, client, flavor);
            Quarantine(sid, "its configuration has not been applied yet.");
            await ConfigureAttachedAsync(sid, client, res, flavor, ct);
            onAttached?.Invoke(sid);
        }
        finally { gate.Release(); }

        _logger.LogInformation("New ACP session {SessionId} cwd={Cwd} flavor={Flavor}", sid, cwd, flavor.Key);
        return sid;
    }

    public async Task LoadSessionAsync(
        string sessionId,
        string cwd,
        AcpFlavor flavor,
        CancellationToken ct,
        Action<string>? onAttached = null)
    {
        var gate = await AcquireSessionGateAsync(sessionId, ct);
        try
        {
            await LoadSessionCoreAsync(sessionId, cwd, flavor, ct, onAttached);
        }
        finally { gate.Release(); }
    }

    private async Task LoadSessionCoreAsync(
        string sessionId,
        string cwd,
        AcpFlavor flavor,
        CancellationToken ct,
        Action<string>? onAttached)
    {
        if (_sessionClient.ContainsKey(sessionId))
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is already attached to an ACP child.");
        }
        if (_resident.ContainsKey(sessionId))
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is still resident in an ACP child; recycle that child before loading it again.");
        }

        var client = await _acquireClient(flavor, ct);
        JsonNode? res;
        try
        {
            res = await client.CallAsync("session/load", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["cwd"] = cwd,
                ["mcpServers"] = new JsonArray(),
            }, ct, timeoutSec: 300);
        }
        catch (Exception loadException)
        {
            // The JSON-RPC request was already written. Cancellation/timeout only
            // stops our wait; the child may still complete session/load and keep
            // the session resident. Track that possibility, then recycle the
            // exact child generation before any retry. If a healthy co-hosted
            // turn vetoes recycle, the resident quarantine remains and a later
            // ReloadFromDiskAsync will retry the eviction safely.
            await TrackIndeterminateLoadAsync(sessionId, client, flavor);
            try
            {
                _ = await TryRecycleHoldingChildAsync(
                    client,
                    flavor,
                    $"session/load for {sessionId} failed or was cancelled",
                    allowedInFlightSessions: null,
                    allowedInFlightValidator: null,
                    CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(
                    cleanupException,
                    "Cleaning up indeterminate session/load for {Sid} failed; the session remains non-promptable",
                    sessionId);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(loadException).Throw();
            throw;
        }

        await AttachRoutingAsync(sessionId, client, flavor);
        Quarantine(sessionId, "its configuration has not been applied yet.");
        await ConfigureAttachedAsync(sessionId, client, res, flavor, ct);
        onAttached?.Invoke(sessionId);
    }

    private async Task TrackIndeterminateLoadAsync(
        string sessionId,
        AcpClient client,
        AcpFlavor flavor)
    {
        await _routingGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_retiredClients.TryGetValue(client, out _))
            {
                _pendingConfigState.TryRemove((client, sessionId), out _);
                return;
            }

            lock (_configStateLock)
            {
                _sessionFlavor[sessionId] = flavor;
                _resident[sessionId] = client;
                MarkOurPid(sessionId, client.ProcessId);
                Quarantine(sessionId, "session/load did not complete verifiably; its child must be recycled before retry.");
            }
        }
        finally { _routingGate.Release(); }
    }

    /// <summary>
    /// Re-attach a session that may still be resident in one of our ACP children.
    /// Copilot never re-reads events.jsonl for a session it already has in
    /// memory, so the holding child is recycled/disposed first; only then does
    /// session/load genuinely pick up whatever another driver wrote while we were
    /// detached. Routing and ownership are restored only after the load AND the
    /// requested configuration have been applied and verified.
    /// </summary>
    public async Task ReloadFromDiskAsync(
        string sessionId,
        string cwd,
        AcpFlavor flavor,
        CancellationToken ct,
        Action<string>? onAttached = null)
    {
        var gate = await AcquireSessionGateAsync(sessionId, ct);
        try
        {
            if (!await EvictResidentAsync(sessionId, ct))
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} is still resident in an ACP child that has a co-hosted turn in " +
                    "flight; recycling it now would kill that turn. Retry once the child is idle.");
            }
            await LoadSessionCoreAsync(sessionId, cwd, flavor, ct, onAttached);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Apply configuration to a session that is already loaded and Owned. This
    /// deliberately does not call session/load (which is non-idempotent). The
    /// latest complete configOptions snapshot is used to resolve values and every
    /// set response refreshes that snapshot. Serialised per session, and the full
    /// process-scope/model/reasoning tuple is re-verified against that snapshot
    /// before the session is released for traffic.
    /// </summary>
    public async Task ApplyOwnedConfigurationAsync(
        string sessionId,
        AcpFlavor requested,
        bool processScopeSpecified,
        CancellationToken ct)
    {
        var gate = await AcquireSessionGateAsync(sessionId, ct);
        try
        {
            if (!_sessionClient.TryGetValue(sessionId, out var client) ||
                !_sessionFlavor.TryGetValue(sessionId, out var loadedFlavor))
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} is marked Owned but has no ACP routing state.");
            }

            if (processScopeSpecified && !SameMcpScope(
                    loadedFlavor.DisabledMcpServers,
                    requested.DisabledMcpServers))
            {
                throw new SessionConfigurationException(
                    $"Session {sessionId} is already loaded with disabled MCP servers " +
                    $"{FormatMcpScope(loadedFlavor.DisabledMcpServers)}; requested " +
                    $"{FormatMcpScope(requested.DisabledMcpServers)} requires a different ACP child. " +
                    "Detach and reload the session to change process-scoped MCP settings.");
            }

            if (requested.Model is null && requested.ReasoningEffort is null)
            {
                // Nothing to re-verify. A quarantined session stays quarantined:
                // we cannot vouch for its live configuration without being told
                // what it is supposed to be and proving it.
                if (!IsQuarantined(sessionId))
                    return;
                throw new SessionConfigurationException(
                    $"Session {sessionId} is quarantined because its configuration could not be verified; " +
                    "re-adopt it supplying the intended model/reasoning so it can be re-applied and confirmed.");
            }

            if (!_sessionConfig.TryGetValue(sessionId, out var config))
            {
                throw new SessionConfigurationException(
                    $"ACP configOptions state for already-loaded session {sessionId} is unavailable; " +
                    "cannot apply or verify the requested session configuration.");
            }

            var state = new JsonObject { ["configOptions"] = config.Options.DeepClone() };
            await ConfigureAttachedAsync(sessionId, client, state, requested, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Apply the requested model/reasoning to an already-routed session and clear
    /// its quarantine only once the resulting configuration verifies end to end.
    /// Any failure leaves the route in place -- the child still holds the session
    /// -- but keeps it quarantined when the child's state is (or may be)
    /// something other than what we would advertise. A caller cancellation that
    /// lands before any mutation is left un-quarantined: nothing changed, so the
    /// prior verified configuration still stands.
    /// </summary>
    private async Task ConfigureAttachedAsync(
        string sessionId,
        AcpClient client,
        JsonNode? configState,
        AcpFlavor requested,
        CancellationToken ct)
    {
        await BeginConfigurationAsync(sessionId, client);
        try
        {
            await ConfigureAttachedCoreAsync(sessionId, client, configState, requested, ct);
        }
        finally
        {
            await EndConfigurationAsync(sessionId, client);
        }
    }

    private async Task ConfigureAttachedCoreAsync(
        string sessionId,
        AcpClient client,
        JsonNode? configState,
        AcpFlavor requested,
        CancellationToken ct)
    {
        var progress = new AcpSessionConfig.ApplyProgress();
        var wasQuarantined = IsQuarantined(sessionId);
        _expectedConfig.TryGetValue(sessionId, out var previousExpected);
        if (requested.Model is not null || requested.ReasoningEffort is not null)
        {
            _expectedConfig[sessionId] = new ExpectedSessionConfig(
                requested.Model ?? previousExpected?.Model,
                requested.ReasoningEffort ?? previousExpected?.ReasoningEffort);
            Quarantine(sessionId, "a configuration update is in progress.");
        }
        try
        {
            if (!client.PublishesOrderedConfigState || !_sessionConfig.ContainsKey(sessionId))
                StoreConfigState(sessionId, configState);
            var applyState = configState;
            if (client.PublishesOrderedConfigState &&
                _sessionConfig.TryGetValue(sessionId, out var latest))
            {
                applyState = new JsonObject { ["configOptions"] = latest.Options.DeepClone() };
            }
            var applied = await AcpSessionConfig.ApplyRequestedAsync(
                applyState,
                sessionId,
                requested.Model,
                requested.ReasoningEffort,
                async (method, @params, token) =>
                {
                    var result = await client.CallAsync(method, @params, token);
                    if (!client.PublishesOrderedConfigState)
                        StoreConfigState(sessionId, result);
                    return result;
                },
                ct,
                progress);
            VerifyAttachedConfiguration(sessionId, requested, applied, previousExpected);
        }
        catch (Exception ex)
        {
            if (ex is SessionConfigurationException configurationException)
                configurationException.SessionId ??= sessionId;
            var indeterminate = progress.Indeterminate
                || (ex as SessionConfigurationException)?.LeavesSessionIndeterminate == true;
            if (indeterminate || wasQuarantined)
            {
                Quarantine(
                    sessionId,
                    indeterminate
                        ? $"its live configuration could not be verified ({ex.Message})"
                        : $"its configuration is still unverified ({ex.Message})");
            }
            else
            {
                // Nothing was mutated (bad request, unavailable option, or a
                // caller cancellation before the first RPC), so the last
                // verified configuration is still the live one.
                if (previousExpected is null)
                    _expectedConfig.TryRemove(sessionId, out _);
                else
                    _expectedConfig[sessionId] = previousExpected;
                lock (_configStateLock)
                {
                    if (previousExpected is null ||
                        (_sessionConfig.TryGetValue(sessionId, out var current) &&
                         MatchesExpected(previousExpected.Model, current.Model) &&
                         MatchesExpected(previousExpected.ReasoningEffort, current.ReasoningEffort)))
                    {
                        ClearQuarantine(sessionId);
                    }
                    else
                    {
                        Quarantine(
                            sessionId,
                            "the verified model/reasoning tuple changed while the rejected update was being checked.");
                    }
                }
            }
            throw;
        }
    }

    private async Task BeginConfigurationAsync(string sessionId, AcpClient client)
    {
        await _routingGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_retiredClients.TryGetValue(client, out _) ||
                !_sessionClient.TryGetValue(sessionId, out var current) ||
                !ReferenceEquals(current, client))
            {
                throw new SessionConfigurationException(
                    $"Session {sessionId} lost its ACP child before configuration could begin.")
                {
                    SessionId = sessionId,
                    LeavesSessionIndeterminate = true,
                };
            }
            _configuring[sessionId] = client;
        }
        finally { _routingGate.Release(); }
    }

    private async Task EndConfigurationAsync(string sessionId, AcpClient client)
    {
        await _routingGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_configuring.TryGetValue(sessionId, out var current) &&
                ReferenceEquals(current, client))
            {
                _configuring.TryRemove(sessionId, out _);
            }
        }
        finally { _routingGate.Release(); }
    }

    /// <summary>
    /// Re-check the whole tuple -- process scope, model, reasoning -- against the
    /// newest snapshot we hold, which includes any config_option_update that
    /// arrived while the set calls were in flight. A set response that confirmed
    /// a value is not enough on its own: a later notification can move it again,
    /// and serving a session on a configuration the caller did not ask for is the
    /// failure this whole path exists to prevent.
    /// </summary>
    private void VerifyAttachedConfiguration(
        string sessionId,
        AcpFlavor requested,
        AcpSessionConfig.AppliedConfig applied,
        ExpectedSessionConfig? previousExpected)
    {
        lock (_configStateLock)
        {
            if (!_sessionClient.ContainsKey(sessionId) ||
                !_sessionFlavor.TryGetValue(sessionId, out var processFlavor))
            {
                throw new SessionConfigurationException(
                    $"Session {sessionId} lost its ACP routing while its configuration was being applied.")
                {
                    LeavesSessionIndeterminate = true,
                };
            }

            if (!SameMcpScope(processFlavor.DisabledMcpServers, requested.DisabledMcpServers) &&
                requested.DisabledMcpServers is { Count: > 0 })
            {
                throw new SessionConfigurationException(
                    $"Session {sessionId} ended up on disabled MCP servers " +
                    $"{FormatMcpScope(processFlavor.DisabledMcpServers)} rather than the requested " +
                    $"{FormatMcpScope(requested.DisabledMcpServers)}.")
                {
                    LeavesSessionIndeterminate = true,
                };
            }

            var canonicalExpected = new ExpectedSessionConfig(
                requested.Model is null ? previousExpected?.Model : applied.Model,
                requested.ReasoningEffort is null ? previousExpected?.ReasoningEffort : applied.ReasoningEffort);
            var requiresConfigState =
                requested.Model is not null ||
                requested.ReasoningEffort is not null ||
                canonicalExpected.Model is not null ||
                canonicalExpected.ReasoningEffort is not null;
            if (requiresConfigState)
            {
                if (!_sessionConfig.TryGetValue(sessionId, out var effective))
                {
                    throw new SessionConfigurationException(
                        $"ACP configOptions state for session {sessionId} is unavailable; " +
                        "cannot verify the requested or retained session configuration.")
                    {
                        LeavesSessionIndeterminate = true,
                    };
                }

                VerifyValue(sessionId, "model", canonicalExpected.Model, effective.Model);
                VerifyValue(sessionId, "reasoning effort", canonicalExpected.ReasoningEffort, effective.ReasoningEffort);
            }
            if (canonicalExpected.Model is null && canonicalExpected.ReasoningEffort is null)
                _expectedConfig.TryRemove(sessionId, out _);
            else
                _expectedConfig[sessionId] = canonicalExpected;
            ClearQuarantine(sessionId);
        }
    }

    private static void VerifyValue(string sessionId, string description, string? expected, string? actual)
    {
        if (expected is null)
            return;
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            return;
        throw new SessionConfigurationException(
            $"Session {sessionId} reports {description} '{actual ?? "(none)"}' after pinning '{expected}'.")
        {
            LeavesSessionIndeterminate = true,
        };
    }

    private async Task AttachRoutingAsync(string sessionId, AcpClient client, AcpFlavor flavor)
    {
        // session/new or session/load has already mutated the child, so this
        // bookkeeping must finish even if the request token was cancelled after
        // the response arrived. The routing gate makes publication atomic with
        // recycle's "retire + snapshot + invalidate" sequence.
        await _routingGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_retiredClients.TryGetValue(client, out _))
            {
                _pendingConfigState.TryRemove((client, sessionId), out _);
                throw new SessionConfigurationException(
                    $"Session {sessionId} completed loading on an ACP child that was recycled before " +
                    "its route could be published; retry the adopt on the replacement child.")
                {
                    SessionId = sessionId,
                    LeavesSessionIndeterminate = true,
                };
            }

            lock (_configStateLock)
            {
                _sessionConfig.TryRemove(sessionId, out _);
                _sessionFlavor[sessionId] = flavor;
                MarkOurPid(sessionId, client.ProcessId);
                if (_pendingConfigState.TryRemove((client, sessionId), out var pending))
                    StoreConfigSnapshotLocked(sessionId, pending);
                // Quarantine before publishing the route: once _sessionClient is
                // visible, prompt admission must already know the configuration
                // is unverified.
                Quarantine(sessionId, "its configuration has not been applied yet.");
                _sessionClient[sessionId] = client;
                _resident[sessionId] = client;
                _freshness.RecordServed(sessionId, EventsPath(sessionId));
            }
        }
        finally { _routingGate.Release(); }
    }

    private void Quarantine(string sessionId, string reason)
    {
        if (_quarantined.TryGetValue(sessionId, out var existing) && existing == reason)
            return;
        _quarantined[sessionId] = reason;
        _logger.LogWarning("Session {Sid} quarantined: {Reason}", sessionId, reason);
    }

    private void ClearQuarantine(string sessionId)
    {
        if (_quarantined.TryRemove(sessionId, out _))
            _logger.LogInformation("Session {Sid} released from quarantine after a verified configuration", sessionId);
    }

    private void StoreConfigState(string sessionId, JsonNode? state)
    {
        var snapshot = CreateConfigSnapshot(state);
        if (snapshot is null)
            return;
        lock (_configStateLock)
            StoreConfigSnapshotLocked(sessionId, snapshot);
    }

    internal void ObserveConfigState(AcpClient source, string sessionId, JsonNode? state)
    {
        var snapshot = CreateConfigSnapshot(state);
        if (snapshot is null)
            return;

        lock (_configStateLock)
        {
            if (_retiredClients.TryGetValue(source, out _))
                return;

            if (_sessionClient.TryGetValue(sessionId, out var current))
            {
                if (ReferenceEquals(source, current))
                    StoreConfigSnapshotLocked(sessionId, snapshot);
                return;
            }

            _pendingConfigState[(source, sessionId)] = snapshot;
        }
    }

    internal void ObserveConfigState(string sessionId, JsonNode? state) =>
        StoreConfigState(sessionId, state);

    private static SessionConfigSnapshot? CreateConfigSnapshot(JsonNode? state)
    {
        if (state?["configOptions"] is not JsonArray options)
            return null;
        var snapshot = (JsonArray)options.DeepClone();
        var current = AcpSessionConfig.ReadCurrentValues(snapshot);
        return new SessionConfigSnapshot(snapshot, current.Model, current.ReasoningEffort);
    }

    private void StoreConfigSnapshotLocked(string sessionId, SessionConfigSnapshot snapshot)
    {
        _sessionConfig[sessionId] = snapshot;
        if (_expectedConfig.TryGetValue(sessionId, out var expected) &&
            (!MatchesExpected(expected.Model, snapshot.Model) ||
             !MatchesExpected(expected.ReasoningEffort, snapshot.ReasoningEffort)))
        {
            Quarantine(
                sessionId,
                $"the child reported model/reasoning '{snapshot.Model ?? "(none)"}'/" +
                $"'{snapshot.ReasoningEffort ?? "(none)"}' instead of the verified requested tuple.");
        }
    }

    private static bool MatchesExpected(string? expected, string? actual) =>
        expected is null || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private void RemoveSessionRouting(
        string sessionId,
        bool removeFlavor,
        bool removeConfig,
        bool removeResidency)
    {
        _sessionClient.TryRemove(sessionId, out _);
        _freshness.Forget(sessionId);
        _ourSessionPids.TryRemove(sessionId, out _);
        _lastEventAt.TryRemove(sessionId, out _);
        _openToolCalls.TryRemove(sessionId, out _);
        _configuring.TryRemove(sessionId, out _);
        _quarantined.TryRemove(sessionId, out _);
        if (removeResidency)
            _resident.TryRemove(sessionId, out _);
        if (removeFlavor)
            _sessionFlavor.TryRemove(sessionId, out _);
        if (removeConfig)
        {
            _sessionConfig.TryRemove(sessionId, out _);
            _expectedConfig.TryRemove(sessionId, out _);
        }
    }

    private sealed record SessionConfigSnapshot(
        JsonArray Options,
        string? Model,
        string? ReasoningEffort);

    private sealed record ExpectedSessionConfig(
        string? Model,
        string? ReasoningEffort);

    private sealed record RetiringSession(
        AcpClient Client,
        AcpFlavor Flavor);

    /// <summary>
    /// Every session a child is holding: the ones we still route to AND the ones
    /// that are merely resident after a detach. Both sets die with the child, so
    /// both must be invalidated together -- dropping only the routed ones would
    /// leave a detached session pointing at a dead process and make the next
    /// re-attach skip the recycle it needs.
    /// </summary>
    private List<string> SessionsHeldBy(AcpClient client) =>
        _sessionClient.Where(kv => ReferenceEquals(kv.Value, client)).Select(kv => kv.Key)
            .Concat(_resident.Where(kv => ReferenceEquals(kv.Value, client)).Select(kv => kv.Key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Kill the child holding a set of sessions -- recycling the pool entry for a
    /// multiplexing flavor, or disposing the dedicated child of a non-multiplexing
    /// one -- then invalidate the routing of every session it held and reap the
    /// now-dead locks so a rescan sees Dormant rather than Locked. Per-session
    /// flavor and config snapshots are kept so each session reloads on its own
    /// process scope with its own model/reasoning.
    /// </summary>
    private async Task<IReadOnlyList<string>?> TryRecycleHoldingChildAsync(
        AcpClient target,
        AcpFlavor flavor,
        string reason,
        IReadOnlySet<string>? allowedInFlightSessions,
        Func<string, bool>? allowedInFlightValidator,
        CancellationToken ct)
    {
        await _routingGate.WaitAsync(ct);
        List<string> held;
        List<string> detached;
        try
        {
            held = SessionsHeldBy(target);
            List<string> busy;
            lock (_activityLock)
            {
                busy = held
                    .Where(s =>
                        (_inFlight.ContainsKey(s) &&
                         !(allowedInFlightSessions?.Contains(s) == true &&
                           (allowedInFlightValidator?.Invoke(s) ?? true))) ||
                        (_configuring.TryGetValue(s, out var configuringClient) &&
                         ReferenceEquals(configuringClient, target)))
                    .ToList();
            }
            if (busy.Count > 0)
            {
                _logger.LogWarning(
                    "Cannot recycle ACP child flavor {Flavor} ({Reason}); {Count} held session(s) are prompting or configuring ({Busy})",
                    flavor.Key, reason, busy.Count, string.Join(", ", busy));
                return null;
            }

            // Sessions we no longer route to are here only because the child
            // still had them in memory. Once it dies that is no longer true of
            // anything, so their bookkeeping goes with it.
            detached = held.Where(s => !_sessionClient.ContainsKey(s)).ToList();

            // From this point on, a late session/new or session/load response is
            // not allowed to publish a route to this client. Once destructive
            // recycle begins it is deliberately non-cancelable: cancellation or
            // replacement startup failure must not leave live-looking routes
            // pointing at a child that may already have been disposed.
            _retiredClients.GetValue(target, static _ => new object());
            foreach (var key in _pendingConfigState.Keys.Where(k => ReferenceEquals(k.Client, target)))
                _pendingConfigState.TryRemove(key, out _);

            // Invalidate every route while prompt/config/attach admission is
            // excluded by the routing gate. The potentially slow process
            // disposal and replacement startup happen only after the gate is
            // released, so unrelated children remain routable throughout.
            foreach (var s in held)
            {
                var forget = detached.Contains(s, StringComparer.Ordinal);
                var sessionFlavor = _sessionFlavor.TryGetValue(s, out var remembered)
                    ? remembered
                    : flavor;
                _retiringSessions[s] = new RetiringSession(target, sessionFlavor);
                RemoveSessionRouting(s, removeFlavor: forget, removeConfig: forget, removeResidency: true);
            }
        }
        finally { _routingGate.Release(); }

        _logger.LogWarning(
            "Recycling ACP child flavor {Flavor} ({Reason}); {Count} held session(s) will reload from disk on next use",
            flavor.Key, reason, held.Count);

        var recycleGate = _clientRecycleGates.GetValue(target, static _ => new SemaphoreSlim(1, 1));
        await recycleGate.WaitAsync(CancellationToken.None);
        try
        {
            try
            {
                if (flavor.MultiplexesSessions)
                {
                    var replacement = await _recycleClient(flavor, target, CancellationToken.None);
                    if (replacement is null)
                    {
                        // The pool had already moved on to another generation.
                        // Dispose only our stale target; never recycle the newer
                        // cached child or invalidate routes it now serves.
                        await target.DisposeAsync();
                    }
                }
                else
                    await target.DisposeAsync(); // per-session child; just kill it
            }
            finally
            {
                foreach (var s in held)
                {
                    if (_retiringSessions.TryGetValue(s, out var retiring) &&
                        ReferenceEquals(retiring.Client, target))
                    {
                        _retiringSessions.TryRemove(s, out _);
                    }
                    try { Magpilot.Agent.Sessions.SessionLocks.ReapDead(Path.Combine(_sessionStateRoot, s)); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Reaping locks for {Sid} during recycle threw", s); }
                }
            }
        }
        finally { recycleGate.Release(); }
        return held;
    }

    /// <summary>
    /// Make sure no live child of ours still has <paramref name="sessionId"/> in
    /// memory, so a following session/load really re-reads disk. Returns false
    /// (without touching anything) when a session co-hosted on that child has a
    /// turn in flight -- recycling then would kill a healthy turn, and the caller
    /// can retry once it settles.
    /// </summary>
    private async Task<bool> EvictResidentAsync(string sessionId, CancellationToken ct)
    {
        if (!_resident.TryGetValue(sessionId, out var holder) &&
            !_sessionClient.TryGetValue(sessionId, out holder))
            return true;

        var flavor = _sessionFlavor.TryGetValue(sessionId, out var remembered)
            ? remembered
            : AcpFlavor.Default;
        return await TryRecycleHoldingChildAsync(
            holder,
            flavor,
            $"re-attaching {sessionId} from disk",
            allowedInFlightSessions: null,
            allowedInFlightValidator: null,
            ct) is not null;
    }

    private static string FormatMcpScope(IReadOnlyList<string>? servers) =>
        servers is null || servers.Count == 0
            ? "(none)"
            : $"[{string.Join(", ", servers)}]";

    private static bool SameMcpScope(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right) =>
        (left ?? []).SequenceEqual(right ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Clear a stale resume by recycling the multiplexing ACP child that holds
    /// <paramref name="sessionId"/>: replace the child generation and reload the
    /// session from disk so current state is served. This is the reliable fallback
    /// when a close is unavailable or indeterminate and <c>session/load</c> would
    /// otherwise reuse an already-loaded snapshot. Every OTHER session the child
    /// multiplexed is dropped from our
    /// routing and its stale lock reaped, so it reads as Dormant and reloads from
    /// disk on next adopt. Refuses (<see cref="RecycleOutcome.Busy"/>) while any
    /// co-hosted session has a turn in flight, so a live turn is never killed.
    /// <paramref name="cwdResolver"/> supplies the reload cwd; the registry backs
    /// it with the session scanner.
    /// </summary>
    public async Task<RecycleOutcome> RecycleForStaleAsync(string sessionId, Func<string, string?> cwdResolver, CancellationToken ct)
    {
        AcpClient target;
        AcpFlavor flavor;
        if (_sessionClient.TryGetValue(sessionId, out target!))
        {
            flavor = _sessionFlavor.TryGetValue(sessionId, out var remembered)
                ? remembered
                : AcpFlavor.Default;
        }
        else if (_retiringSessions.TryGetValue(sessionId, out var retiring))
        {
            target = retiring.Client;
            flavor = retiring.Flavor;
        }
        else
        {
            return RecycleOutcome.NotLoaded;
        }

        if (await TryRecycleHoldingChildAsync(
                target,
                flavor,
                $"clearing stale resume of {sessionId}",
                allowedInFlightSessions: null,
                allowedInFlightValidator: null,
                ct) is null)
            return RecycleOutcome.Busy;

        // Eagerly reload the session that triggered this so the resume serves
        // current state; bystanders self-heal via the Dormant path on next use.
        var cwd = cwdResolver(sessionId) ?? Environment.CurrentDirectory;
        await LoadSessionAsync(sessionId, cwd, flavor, ct);
        return RecycleOutcome.Recycled;
    }

    /// <summary>
    /// True when an in-flight turn has produced no child activity for at least
    /// <paramref name="threshold"/>: nothing has streamed and no tool call has
    /// fired since <paramref name="lastEventAt"/> (or since the turn started, if
    /// it never emitted anything). A hung model request that never returns looks
    /// exactly like this; a legitimately slow turn keeps emitting tool-call /
    /// message-chunk updates, which resets the clock.
    /// </summary>
    internal static bool IsTurnStalled(DateTimeOffset startedAt, DateTimeOffset? lastEventAt, DateTimeOffset now, TimeSpan threshold, bool hasOpenToolCall = false)
    {
        // A turn waiting on a tool it invoked (a long shell command, a slow MCP
        // call) is silent on the stream but not wedged -- never stall it.
        if (hasOpenToolCall) return false;
        var lastActivity = lastEventAt is { } t && t > startedAt ? t : startedAt;
        return now - lastActivity >= threshold;
    }

    /// <summary>
    /// Whether the in-flight turn for <paramref name="sid"/> looks wedged: silent
    /// past <paramref name="threshold"/> AND not currently waiting on a tool call
    /// it invoked. Only a hung model -- nothing streaming, nothing pending -- is
    /// treated as stalled, so a legitimately long tool is never killed.
    /// </summary>
    private bool IsSessionStalled(string sid, InFlightEntry entry, DateTimeOffset now, TimeSpan threshold)
    {
        var hasOpenTool = _openToolCalls.TryGetValue(sid, out var open) && !open.IsEmpty;
        var last = _lastEventAt.TryGetValue(sid, out var l) ? (DateTimeOffset?)l : null;
        return IsTurnStalled(entry.StartedAt, last, now, threshold, hasOpenTool);
    }

    /// <summary>
    /// Find in-flight turns that have wedged (no child activity for
    /// <paramref name="threshold"/>) and recover them: tell subscribers the turn
    /// failed so a waiting caller (phone assistant, SPA, WhatsApp) stops spinning,
    /// then recycle the ACP child holding the session so it becomes usable again.
    /// Without this a hung turn pins the session in flight until the 10-minute
    /// session/prompt timeout and blocks every later turn on that child until the
    /// agent is restarted by hand. A co-hosted session whose turn is still
    /// progressing vetoes the recycle -- the child is shared, so a healthy turn is
    /// never killed as collateral. Returns the number of sessions recovered.
    /// </summary>
    public async Task<int> SweepStalledTurnsAsync(TimeSpan threshold, Func<string, string?> cwdResolver, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var recovered = 0;

        foreach (var (sid, entry) in _inFlight.ToArray())
        {
            if (!IsSessionStalled(sid, entry, now, threshold))
                continue;
            if (!_sessionClient.TryGetValue(sid, out var client))
                continue;

            // Recycling kills the shared child, so never do it while a co-hosted
            // session has a turn that is still making progress or waiting on a
            // tool it invoked.
            var healthyNeighbour = _sessionClient.Any(kv =>
                !string.Equals(kv.Key, sid, StringComparison.Ordinal)
                && ReferenceEquals(kv.Value, client)
                && _inFlight.TryGetValue(kv.Key, out var e2)
                && !IsSessionStalled(kv.Key, e2, now, threshold));
            if (healthyNeighbour)
            {
                _logger.LogWarning(
                    "Turn watchdog: session {Sid} is stalled but a co-hosted session has a live turn; deferring recycle",
                    sid);
                continue;
            }

            var lastAct = _lastEventAt.TryGetValue(sid, out var lg) && lg > entry.StartedAt ? lg : entry.StartedAt;
            var stalledFor = now - lastAct;
            _logger.LogError(
                "Turn watchdog: session {Sid} produced no activity for {Seconds:F0}s (requester={Req}); " +
                "recycling its ACP child and failing the turn",
                sid, stalledFor.TotalSeconds, entry.Requester ?? "(none)");

            // Re-check under fresh state: if the turn finished between the snapshot
            // and here, recycling would needlessly drop the child's other sessions.
            if (!_inFlight.ContainsKey(sid))
                continue;

            var stalledOnChild = _inFlight
                .Where(kv =>
                    _sessionClient.TryGetValue(kv.Key, out var holder) &&
                    ReferenceEquals(holder, client) &&
                    IsSessionStalled(kv.Key, kv.Value, now, threshold))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal);

            try
            {
                if (await RecycleForStalledTurnAsync(
                        sid,
                        client,
                        stalledOnChild,
                        threshold,
                        cwdResolver,
                        ct))
                {
                    foreach (var stalledSession in stalledOnChild)
                    {
                        // PromptAsync also emits TurnComplete(error) once the
                        // recycle faults its session/prompt call, but an explicit
                        // error lets a voice caller speak a failure instead of
                        // ending silent.
                        Publish(
                            stalledSession,
                            new ErrorEvent("The assistant stalled and was reset. Please try again."));
                    }
                    recovered += stalledOnChild.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Turn watchdog: recycling the ACP child for stalled session {Sid} failed", sid);
            }
        }

        return recovered;
    }

    /// <summary>
    /// Kill and respawn the ACP child holding <paramref name="sessionId"/> -- the
    /// only way copilot releases a wedged in-flight turn, since session/cancel is a
    /// notification the hung child never reads -- then reload the session from disk
    /// on the fresh child using its original flavor. Disposing the old child faults
    /// its pending session/prompt call, so <see cref="PromptAsync"/> unwinds and
    /// clears the in-flight entry. Every OTHER session the child multiplexed is
    /// dropped from routing and reloads on next use, mirroring
    /// <see cref="RecycleForStaleAsync"/>.
    /// </summary>
    private async Task<bool> RecycleForStalledTurnAsync(
        string sessionId,
        AcpClient target,
        IReadOnlySet<string> stalledSessions,
        TimeSpan threshold,
        Func<string, string?> cwdResolver,
        CancellationToken ct)
    {
        var flavor = _sessionFlavor.TryGetValue(sessionId, out var f) ? f : AcpFlavor.Default;

        if (await TryRecycleHoldingChildAsync(
                target,
                flavor,
                $"recovering stalled turn on {sessionId}",
                allowedInFlightSessions: stalledSessions,
                allowedInFlightValidator: stalledSession =>
                    _inFlight.TryGetValue(stalledSession, out var entry) &&
                    IsSessionStalled(
                        stalledSession,
                        entry,
                        DateTimeOffset.UtcNow,
                        threshold),
                ct) is null)
            return false;

        var cwd = cwdResolver(sessionId) ?? Environment.CurrentDirectory;
        await LoadSessionAsync(sessionId, cwd, flavor, ct);
        return true;
    }

    /// <summary>
    /// True if a resume would be served a stale in-memory snapshot: the session's
    /// on-disk events have grown past what our child last synced. A foreign writer
    /// that has since exited still left us behind, so this does NOT require a live
    /// foreign holder. Our own child's asynchronous post-turn disk flush is
    /// absorbed by <see cref="ResyncAfterSettleAsync"/> (settle-then-record) so it
    /// isn't mistaken for a foreign advance here.
    /// </summary>
    public bool MayBeStale(string sessionId) =>
        _freshness.MayBeStale(sessionId, EventsPath(sessionId));

    /// <summary>
    /// Resync the freshness watermark to the current on-disk size. Called after a
    /// resume that did not reload, to absorb our own child's async post-turn flush
    /// so it isn't mistaken for a foreign advance next time.
    /// </summary>
    public void ResyncWatermark(string sessionId) =>
        _freshness.RecordServed(sessionId, EventsPath(sessionId));

    /// <summary>
    /// Resync the freshness watermark after a turn once copilot has finished its
    /// asynchronous post-turn flush. Polls events.jsonl until its size stops
    /// growing, then records it, so our own turn's flushed tail (final chunks +
    /// usage_update) is not later mistaken for a foreign advance. Bounded so a
    /// runaway (or a concurrent foreign writer) can never leak the task; a foreign
    /// write that lands inside the settle window is absorbed into our watermark --
    /// a narrow race, and the next foreign write is still caught.
    /// </summary>
    private async Task ResyncAfterSettleAsync(string sessionId)
    {
        try
        {
            var path = EventsPath(sessionId);
            long last = -1;
            var stable = 0;
            for (var i = 0; i < 40 && stable < 3; i++) // <= ~20s; settle after ~1.5s stable
            {
                await Task.Delay(500);
                var size = Magpilot.Agent.Sessions.SessionFreshness.Watermark(path);
                if (size == last) stable++;
                else { stable = 0; last = size; }
            }
            _freshness.RecordServed(sessionId, path);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// True if a live lock on this session is held by a process that is not one of
    /// our children for it (a genuinely foreign holder). Used to warn that a
    /// recycled session may go stale again while another process keeps writing.
    /// </summary>
    public bool HasForeignLiveHolder(string sessionId)
    {
        var dir = Path.Combine(_sessionStateRoot, sessionId);
        var ours = _ourSessionPids.TryGetValue(sessionId, out var set) ? set : null;
        return Magpilot.Agent.Sessions.SessionLocks.Foreign(
            Magpilot.Agent.Sessions.SessionLocks.Inspect(dir),
            pid => ours is not null && ours.ContainsKey(pid)).Count > 0;
    }

    /// <summary>
    /// Force-kill every genuinely-foreign live lock holder on this session and
    /// reap the (now-dead) advisory locks, returning the pids evicted. A foreign
    /// holder is a live <c>inuse.&lt;pid&gt;.lock</c> whose pid is not one of our
    /// own ACP children for this session -- i.e. a launcher's interactive copilot
    /// (or a stray <c>copilot --resume</c>) that never let go. copilot exposes no
    /// working <c>session/close</c>, so killing the process is the only way to
    /// drop its lock and guarantee a single writer before we <c>session/load</c>.
    /// Our own child is never a candidate: <see cref="_ourSessionPids"/> is
    /// populated the instant the child attaches (session/new + LoadSession) and
    /// cleared only alongside its lock removal (close/recycle), so a live holder
    /// absent from that set is always foreign.
    /// </summary>
    public IReadOnlyList<int> EvictForeignLiveHolders(string sessionId)
    {
        var dir = Path.Combine(_sessionStateRoot, sessionId);
        var ours = _ourSessionPids.TryGetValue(sessionId, out var set) ? set : null;
        var foreign = Magpilot.Agent.Sessions.SessionLocks.Foreign(
                Magpilot.Agent.Sessions.SessionLocks.Inspect(dir),
                pid => ours is not null && ours.ContainsKey(pid))
            .Select(h => h.Pid)
            .ToList();

        foreach (var pid in foreign)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                _logger.LogWarning("Evicting live foreign holder PID {Pid} on {Sid} (forceful host take-back)", pid, sessionId);
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not evict foreign holder PID {Pid} on {Sid}", pid, sessionId); }
        }

        if (foreign.Count > 0)
        {
            try { Magpilot.Agent.Sessions.SessionLocks.ReapDead(dir); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reaping locks after eviction on {Sid} threw", sessionId); }
        }
        return foreign;
    }

    private void MarkOurPid(string sessionId, int? pid)
    {
        if (pid is int p)
            _ourSessionPids.GetOrAdd(sessionId, _ => new()).TryAdd(p, 0);
    }

    public async Task<Task> StartPromptAsync(
        string sessionId,
        string text,
        CancellationToken reservationCt,
        CancellationToken promptCt,
        string? requester = null,
        string? source = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var gate = await AcquireSessionGateAsync(sessionId, reservationCt);
        try
        {
            await _routingGate.WaitAsync(reservationCt);
        }
        catch
        {
            gate.Release();
            throw;
        }

        try
        {
            AcpClient client;
            lock (_configStateLock)
            {
                if (_draining.ContainsKey(sessionId))
                {
                    throw new InvalidOperationException(
                        $"Session {sessionId} is draining for host handoff and is not accepting new prompts.");
                }
                client = ClientForAsync(sessionId, reservationCt).GetAwaiter().GetResult();
                _turnDone[sessionId] = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[sessionId] = new InFlightEntry(requester, DateTimeOffset.UtcNow);
            }
            return RunPromptWithLeaseAsync(sessionId, text, client, gate, promptCt, requester, source);
        }
        catch
        {
            gate.Release();
            throw;
        }
        finally { _routingGate.Release(); }
    }

    public async Task PromptAsync(string sessionId, string text, CancellationToken ct, string? requester = null, string? source = null)
    {
        try
        {
            var prompt = await StartPromptAsync(sessionId, text, ct, ct, requester, source);
            await prompt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "session/prompt failed before it could start for {Sid}", sessionId);
            Publish(sessionId, new TurnComplete("error"));
        }
    }

    private async Task RunPromptWithLeaseAsync(
        string sessionId,
        string text,
        AcpClient client,
        SemaphoreSlim gate,
        CancellationToken ct,
        string? requester,
        string? source)
    {
        var stopReason = "end_turn";
        try
        {
            _logger.LogDebug("PromptAsync sid={Sid} len={Len} requester={Requester} source={Source}", sessionId, text.Length, requester ?? "(null)", source ?? "(null)");
            // Tag the prompt with its originating surface so the brain can read
            // provenance (ACP has no per-message metadata channel, so an inline tag
            // is the only way). The same tagged text is echoed to subscribers below.
            var promptText = string.IsNullOrEmpty(source) ? text : $"[via {source}] {text}";
            // When a source is set the send is out-of-band (e.g. the phone assistant
            // relaying into the main session). ACP emits user_message_chunk only during
            // load replay, never for live prompts, so echo the tagged question into the
            // broadcast channel -- otherwise a persistent watcher (WhatsApp) or the SPA
            // would see the answer with no question. Sourceless prompts skip this: the
            // SPA self-echoes its own sends, so synthesizing here would double-render.
            if (!string.IsNullOrEmpty(source))
                Publish(sessionId, new UserDelta(promptText, source));

            // Keep the ACP call itself independent of the HTTP/request token.
            // Cancelling our local wait does not cancel a JSON-RPC request that
            // has already been written to the child; we must retain the session
            // lease until the child confirms a boundary or its exact generation
            // is recycled.
            var promptCall = client.CallAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = promptText },
                },
            }, CancellationToken.None, timeoutSec: 600);
            try
            {
                var resp = await promptCall.WaitAsync(ct);
                stopReason = resp?["stopReason"]?.GetValue<string>() ?? stopReason;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stopReason = "error";
                try
                {
                    await client.NotifyAsync(
                        "session/cancel",
                        new JsonObject { ["sessionId"] = sessionId });
                }
                catch (Exception cancelException)
                {
                    _logger.LogWarning(
                        cancelException,
                        "session/cancel failed after local prompt cancellation for {Sid}",
                        sessionId);
                }

                if (!await PromptReachedBoundaryAsync(promptCall, TimeSpan.FromSeconds(30)))
                {
                    await RecycleIndeterminatePromptAsync(sessionId, client);
                }
            }
            catch (TimeoutException)
            {
                stopReason = "error";
                await RecycleIndeterminatePromptAsync(sessionId, client);
            }
        }
        catch (Exception ex)
        {
            stopReason = "error";
            _logger.LogWarning(ex, "session/prompt failed for {Sid}", sessionId);
        }
        finally
        {
            lock (_activityLock)
            {
                _inFlight.TryRemove(sessionId, out _);
                _openToolCalls.TryRemove(sessionId, out _);
            }
            // Wake anyone waiting for the turn to finish.
            if (_turnDone.TryRemove(sessionId, out var tcs))
                tcs.TrySetResult();
            // Release the lease only after the outgoing turn's bookkeeping is
            // gone. Otherwise a queued turn can publish its own _inFlight entry
            // and have this turn immediately erase it.
            gate.Release();
        }

        // Notify subscribers so the SPA can clear its busy/thinking flags.
        Publish(sessionId, new TurnComplete(stopReason));

        // Our child just advanced the session on disk; resync the freshness
        // watermark so its own writes don't later read as a foreign advance.
        // Record immediately, then settle: copilot flushes the turn tail (final
        // chunks + usage_update) asynchronously after session/prompt returns, so
        // a one-shot record here can miss the tail and later read as a foreign
        // advance. The settle task waits for the file to stop growing and records
        // again.
        _freshness.RecordServed(sessionId, EventsPath(sessionId));
        _ = ResyncAfterSettleAsync(sessionId);
    }

    private static async Task<bool> PromptReachedBoundaryAsync(
        Task<JsonNode?> promptCall,
        TimeSpan grace)
    {
        try
        {
            await promptCall.WaitAsync(grace);
            return true;
        }
        catch (TimeoutException)
        {
            // Whether the grace wait expired or the ACP call's own timeout fired,
            // no child-side turn boundary was confirmed.
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            // A JSON-RPC error response or child exit completes the pending call;
            // either way the old child is no longer silently running this turn.
            return true;
        }
    }

    private async Task RecycleIndeterminatePromptAsync(string sessionId, AcpClient client)
    {
        var flavor = EffectiveFlavor(sessionId) ?? AcpFlavor.Default;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { sessionId };
        while (await TryRecycleHoldingChildAsync(
                   client,
                   flavor,
                   $"prompt for {sessionId} ended locally without a confirmed child boundary",
                   allowedInFlightSessions: allowed,
                   allowedInFlightValidator: null,
                   CancellationToken.None) is null)
        {
            // A healthy co-hosted prompt/configuration vetoes recycle. Keep this
            // session in-flight and retain its gate until that work reaches a
            // boundary; then retry rather than permit overlapping writers.
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Push a synthesized event into the broadcast channel for a session.
    /// Used by sidecar code paths (e.g. quick-prompt with a pinned sessionId)
    /// to make a UserDelta visible to other connected subscribers (the SPA),
    /// since ACP doesn't echo the prompt text back during live prompts --
    /// it only emits user_message_chunk during session/load history replay.
    /// </summary>
    public void PublishToSubscribers(string sessionId, StreamEvent evt)
        => Publish(sessionId, evt);

    private void Publish(string sessionId, StreamEvent evt)
    {
        List<Channel<StreamEvent>>? list;
        lock (_subLock) _subscribers.TryGetValue(sessionId, out list);
        if (list is null) return;
        foreach (var ch in list)
            ch.Writer.TryWrite(evt);
    }

    public async Task CancelAsync(string sessionId, CancellationToken ct)
    {
        var client = await ClientForAsync(sessionId, ct);
        await client.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId });
        await Task.CompletedTask;
    }

    /// <summary>
    /// Detach a session from this agent's ACP child. Calls
    /// <c>session/close</c> over JSON-RPC and then sweeps the
    /// session directory to remove the agent's
    /// <c>inuse.&lt;acp-pid&gt;.lock</c> file. The lock removal is
    /// the load-bearing step for cooperative handoff: even though
    /// the ACP child still has the session in memory, the on-disk
    /// lock is what other copilot processes (a launcher's
    /// interactive child, terminal-driven <c>copilot --resume</c>,
    /// etc.) consult to decide whether the session is "in use".
    /// Without this cleanup, every launcher startup against a
    /// session the agent loaded prints a "session is locked by
    /// another process" warning and the new copilot child appends
    /// its own lock alongside (multi-lock advisory state).
    /// </summary>
    /// <param name="sessionId">The session being detached.</param>
    /// <param name="sessionsRoot">
    /// The Copilot CLI's session-state root directory (typically
    /// <c>~/.copilot/session-state</c>). Pass null to skip the lock
    /// cleanup -- only meaningful for the in-process unit tests.
    /// </param>
    public async Task<AcpFlavor?> CloseAsync(
        string sessionId,
        string? sessionsRoot,
        CancellationToken ct)
    {
        // Serialised with attach/configure: detaching a session out from under an
        // in-flight configure would leave the child holding a half-applied config
        // we no longer track.
        var gate = await AcquireSessionGateAsync(sessionId, ct);
        AcpFlavor? flavor = null;
        try
        {
            // Capture the client's PID BEFORE the CloseAsync call, since
            // we need it to identify which lock file to delete and the
            // call might null-out our cache mapping on success.
            AcpClient? client = null;
            _sessionClient.TryGetValue(sessionId, out client);
            flavor = EffectiveFlavor(sessionId);
            var clientPid = client?.ProcessId;
            var closeSucceeded = false;

            try
            {
                if (client is not null)
                {
                    await client.CallAsync("session/close", new JsonObject { ["sessionId"] = sessionId }, ct, timeoutSec: 30);
                    closeSucceeded = true;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "session/close failed for {Sid}", sessionId); }
            finally
            {
                if (sessionsRoot is not null && clientPid is int pid)
                {
                    try
                    {
                        var lockFile = Path.Combine(sessionsRoot, sessionId, $"inuse.{pid}.lock");
                        if (File.Exists(lockFile))
                        {
                            File.Delete(lockFile);
                            _logger.LogInformation("Removed lock {File} after detach (pid={Pid})", lockFile, pid);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to remove lock for session {Sid}", sessionId);
                    }
                }
                // A confirmed close releases the child's in-memory session. If the
                // RPC failed or timed out, retain residency + flavor so handback can
                // recycle the child before loading the session from disk.
                RemoveSessionRouting(
                    sessionId,
                    removeFlavor: false,
                    removeConfig: true,
                    removeResidency: closeSucceeded);
            }
        }
        finally { gate.Release(); }
        return flavor;
    }

    /// <summary>
    /// Force a session away from a prompt that ignored cancellation by killing
    /// the ACP child that is still capable of writing it. For a multiplex child
    /// this invalidates every idle co-hosted route; an active co-host vetoes the
    /// takeover rather than being killed as collateral.
    /// </summary>
    public async Task<AcpFlavor?> ForceDetachAsync(string sessionId, CancellationToken ct)
    {
        if (!_sessionClient.TryGetValue(sessionId, out var client) &&
            !_resident.TryGetValue(sessionId, out client))
            return EffectiveFlavor(sessionId);

        var flavor = EffectiveFlavor(sessionId) ?? AcpFlavor.Default;
        var recycled = await TryRecycleHoldingChildAsync(
            client,
            flavor,
            $"force-detaching {sessionId} after its turn ignored cancellation",
            allowedInFlightSessions: new HashSet<string>(StringComparer.Ordinal) { sessionId },
            allowedInFlightValidator: null,
            ct);
        if (recycled is null)
        {
            throw new InvalidOperationException(
                $"Cannot force-detach session {sessionId} because another session on its shared ACP child has a turn in flight.");
        }
        return flavor;
    }

    public ChannelReader<StreamEvent> Subscribe(string sessionId)
    {
        var ch = Channel.CreateUnbounded<StreamEvent>(new UnboundedChannelOptions { SingleReader = true });
        int count;
        lock (_subLock)
        {
            if (!_subscribers.TryGetValue(sessionId, out var list))
                _subscribers[sessionId] = list = new();
            list.Add(ch);
            count = list.Count;
        }
        if (count > 1)
            _logger.LogWarning("Subscribe sid={Sid} -> {Count} subscribers (>1 means multiple SSE connections, expect duplicate UI events)", sessionId, count);
        else
            _logger.LogDebug("Subscribe sid={Sid} -> {Count}", sessionId, count);
        return ch.Reader;
    }

    public void Unsubscribe(string sessionId, ChannelReader<StreamEvent> reader)
    {
        int count = 0;
        lock (_subLock)
        {
            if (_subscribers.TryGetValue(sessionId, out var list))
            {
                list.RemoveAll(c => ReferenceEquals(c.Reader, reader));
                count = list.Count;
                if (list.Count == 0) _subscribers.Remove(sessionId);
            }
        }
        _logger.LogDebug("Unsubscribe sid={Sid} -> {Count}", sessionId, count);
    }

    public bool ResolveApproval(string approvalId, string optionId)
    {
        TaskCompletionSource<ApprovalResponse>? tcs;
        lock (_approvalLock) _pendingApprovals.Remove(approvalId, out tcs);
        return tcs?.TrySetResult(new ApprovalResponse(optionId)) ?? false;
    }

    internal void HandleUpdate(string sessionId, JsonNode? update)
    {
        if (update is null) return;
        var kind = update["sessionUpdate"]?.GetValue<string>();
        lock (_activityLock)
        {
            // Any update from the child means this session's turn is making
            // progress. Record it so the watchdog can distinguish a live-but-slow
            // turn from a wedged one that emits nothing.
            _lastEventAt[sessionId] = DateTimeOffset.UtcNow;
            // Track open tool calls so the watchdog can tell a turn waiting on a
            // long tool (silent between pending and completed updates) apart from
            // a wedged model.
            if (kind == "tool_call")
            {
                var tid = update["toolCallId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(tid))
                    _openToolCalls.GetOrAdd(sessionId, static _ => new()).TryAdd(tid, 0);
            }
            else if (kind == "tool_call_update"
                     && update["status"]?.GetValue<string>() is "completed" or "failed")
            {
                var tid = update["toolCallId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(tid) && _openToolCalls.TryGetValue(sessionId, out var openSet))
                    openSet.TryRemove(tid, out _);
            }
        }
        _logger.LogDebug("HandleUpdate sid={Sid} kind={Kind}", sessionId, kind);
        StreamEvent? evt = kind switch
        {
            "agent_message_chunk" => new AssistantDelta(ExtractText(update["content"]) ?? ""),
            "agent_thought_chunk" => new ThoughtDelta(ExtractText(update["content"]) ?? ""),
            "user_message_chunk"  => new UserDelta(ExtractText(update["content"]) ?? ""),
            // ACP uses `tool_call` (status: pending) for new tool calls
            // and `tool_call_update` (status: in_progress | completed |
            // failed) for subsequent updates -- NOT the *_start / *_end
            // suffix variants. Map both to our existing StreamEvent
            // surface so SSE consumers (SPA, WhatsApp sidecar) see clean
            // ToolCallStart/End/Progress events at the right boundaries.
            "tool_call" => new ToolCallStart(
                update["toolCallId"]?.GetValue<string>() ?? "",
                update["title"]?.GetValue<string>() ?? update["kind"]?.GetValue<string>() ?? "tool",
                update["rawInput"]?.ToJsonString()),
            "tool_call_update" => MapToolCallUpdate(update),
            _ => null,
        };
        if (evt is null)
        {
            // Unknown sessionUpdate kind -- surface at Warning so a new
            // ACP addition that we'd otherwise silently drop shows up in
            // /admin/logs (instead of waiting for a user-visible symptom
            // like "the SPA stopped reflecting some new event type").
            // Kinds we knowingly ignore (available_commands_update,
            // config_option_update, plan, current_mode_update, usage_update)
            // are common enough to be noisy -- whitelist them. usage_update is
            // per-turn context telemetry ({used, size}); mapping it to a SPA
            // context meter is a possible future enhancement. Anything else is news.
            if (kind is not null
                && kind is not "available_commands_update"
                && kind is not "config_option_update"
                && kind is not "plan"
                && kind is not "current_mode_update"
                && kind is not "usage_update")
            {
                var raw = update.ToJsonString();
                if (raw.Length > 400) raw = raw[..400] + "...";
                _logger.LogWarning(
                    "HandleUpdate unknown sessionUpdate kind={Kind} sid={Sid} raw={Raw}",
                    kind, sessionId, raw);
            }
            return;
        }

        // Copilot CLI leaks file-operation notices ("Info: <abs-path>") into the
        // agent message stream as standalone agent_message_chunks; forwarded as
        // assistant text they garble the reply bubble. Drop them as a client-side
        // guard -- the root cause is the CLI's ACP output, not the model.
        if (evt is AssistantDelta ad && IsInfoPathBleed(ad.Text))
        {
            _logger.LogDebug("Dropped Info: path notice bled into agent_message_chunk sid={Sid} text={Text}",
                sessionId, ad.Text);
            return;
        }

        List<Channel<StreamEvent>>? list;
        lock (_subLock) _subscribers.TryGetValue(sessionId, out list);
        if (list is null) return;
        foreach (var ch in list)
            ch.Writer.TryWrite(evt);
    }

    internal static bool IsInfoPathBleed(string text)
    {
        // Fires only on a standalone "Info: <path>" notice, never on prose that
        // merely contains the word "Info". Path shapes: "Info: <drive>:\..." or
        // "Info: <drive>:/..." (Windows) and "Info: /..." (Unix).
        if (!text.StartsWith("Info: ", StringComparison.Ordinal)) return false;
        if (text.Length < 8) return false;
        var rest = text.AsSpan(6); // skip "Info: "
        if (rest.Length >= 3 && char.IsLetter(rest[0]) && rest[1] == ':' && (rest[2] == '\\' || rest[2] == '/')) return true;
        if (rest.Length >= 1 && rest[0] == '/') return true;
        return false;
    }

    private static StreamEvent MapToolCallUpdate(JsonNode update)
    {
        var id = update["toolCallId"]?.GetValue<string>() ?? "";
        var status = update["status"]?.GetValue<string>();
        if (status is "completed" or "failed")
        {
            return new ToolCallEnd(
                id,
                update["rawOutput"]?.ToJsonString(),
                status == "completed");
        }
        return new ToolCallProgress(id, update["content"]?.ToJsonString());
    }

    private static string? ExtractText(JsonNode? content)
    {
        if (content is null) return null;
        if (content is JsonObject obj && obj["text"] is JsonNode t) return t.GetValue<string>();
        return content.ToString();
    }

    private async Task<JsonNode> HandleRequestAsync(string method, JsonNode? @params)
    {
        if (method != "session/request_permission")
            return new JsonObject();

        var sessionId = @params?["sessionId"]?.GetValue<string>() ?? "";
        // A permission request is child activity too -- a turn paused on an
        // approval is waiting on the user, not wedged.
        if (sessionId.Length > 0) _lastEventAt[sessionId] = DateTimeOffset.UtcNow;
        var optsArr = @params?["options"] as JsonArray ?? new JsonArray();
        var options = new List<ApprovalOption>();
        foreach (var o in optsArr)
        {
            if (o is null) continue;
            options.Add(new ApprovalOption(
                o["optionId"]?.GetValue<string>() ?? "",
                o["name"]?.GetValue<string>() ?? o["optionId"]?.GetValue<string>() ?? "?",
                o["kind"]?.GetValue<string>()
            ));
        }

        // Auto-approve fast path. Two independent ways to trigger,
        // each with its own ergonomics:
        //
        //   * Per-session yolo flag (YoloRegistry) -- set by the SPA's
        //     per-session toggle for a session the user explicitly
        //     opted in. Picks `allow_once` so flipping yolo OFF restores
        //     manual approval immediately for any new permission
        //     request. `allow_always` would persist the decision in the
        //     CLI's per-session policy memory, so previously-touched
        //     paths/tools would silently stay allowed even after yolo
        //     is turned off -- surprising and incorrect for an
        //     opt-in/opt-out toggle.
        //
        //   * MAGPILOT_AUTO_APPROVE=true env var -- legacy host-wide
        //     fallback for always-on container agents like Magnus
        //     where there's no human at a SPA to click "approve" and
        //     re-prompting on every call would slow the agent down.
        //     Picks `allow_always` so a long-running session caches
        //     decisions (the same behaviour this shortcut has always
        //     had).
        //
        // Either source short-circuits the SSE approval round-trip
        // the same way. The host-level MAGPILOT_YOLO_DISABLED guard is
        // enforced inside YoloRegistry (IsEnabled is always false), so
        // we never need to check it here.
        var perSessionYolo = _yolo.IsEnabled(sessionId);
        var envWideAutoApprove = string.Equals(
            Environment.GetEnvironmentVariable("MAGPILOT_AUTO_APPROVE"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (perSessionYolo || envWideAutoApprove)
        {
            var pick = perSessionYolo ? PickAllow(options, sticky: false) : PickAllow(options, sticky: true);
            var source = perSessionYolo ? "yolo" : "MAGPILOT_AUTO_APPROVE";
            _logger.LogInformation(
                "Auto-approving permission request for session {Sid} -> {OptionId} ({Source})",
                sessionId, pick, source);
            return BuildOutcome(pick, options);
        }

        var approvalId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ApprovalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_approvalLock) _pendingApprovals[approvalId] = tcs;

        var req = new ApprovalRequired(
            approvalId,
            @params?["toolCall"]?["title"]?.GetValue<string>() ?? "Permission required",
            @params?["toolCall"]?.ToJsonString(),
            options
        );

        List<Channel<StreamEvent>>? list;
        lock (_subLock) _subscribers.TryGetValue(sessionId, out list);
        if (list is not null)
            foreach (var ch in list) ch.Writer.TryWrite(req);

        // Wait up to 5 minutes for a client decision; default deny.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
            if (done != tcs.Task)
            {
                lock (_approvalLock) _pendingApprovals.Remove(approvalId);
                _logger.LogWarning("Approval {Id} timed out, denying", approvalId);
                return BuildOutcome("reject_once", options);
            }
            var resp = await tcs.Task;
            return BuildOutcome(resp.OptionId, options);
        }
        catch
        {
            lock (_approvalLock) _pendingApprovals.Remove(approvalId);
            return BuildOutcome("reject_once", options);
        }
    }

    /// <summary>
    /// Pick the most permissive "allow" option from the offered set.
    /// Prefers allow_always (so the model doesn't keep asking for the
    /// same kind of action), falls back to allow_once, then the first
    /// option that contains "allow" in its id, then the first option,
    /// then a literal "allow_once" string as a last resort.
    /// </summary>
    /// <summary>
    /// Pick an "allow"-flavored option from the ACP permission
    /// request's option list.
    /// </summary>
    /// <param name="sticky">
    /// True: prefer <c>allow_always</c> so the Copilot CLI caches the
    /// decision for the rest of the session and stops re-asking.
    /// Right for unattended sidecars (env-wide MAGPILOT_AUTO_APPROVE).
    /// False: prefer <c>allow_once</c> so each request stays individually
    /// approved. Right for the user-facing per-session yolo toggle,
    /// where flipping yolo off should immediately restore manual
    /// approval for any new permission request -- including for tools
    /// or paths the agent already touched while yolo was on.
    /// </param>
    private static string PickAllow(IReadOnlyList<ApprovalOption> options, bool sticky)
    {
        if (sticky)
        {
            var always = options.FirstOrDefault(o => o.OptionId == "allow_always");
            if (always is not null) return always.OptionId;
            var fallbackOnce = options.FirstOrDefault(o => o.OptionId == "allow_once");
            if (fallbackOnce is not null) return fallbackOnce.OptionId;
        }
        else
        {
            var once = options.FirstOrDefault(o => o.OptionId == "allow_once");
            if (once is not null) return once.OptionId;
            // No allow_once exposed? Fall back to allow_always so we
            // don't block the turn; the comment on the call site
            // documents the tradeoff.
            var fallbackAlways = options.FirstOrDefault(o => o.OptionId == "allow_always");
            if (fallbackAlways is not null) return fallbackAlways.OptionId;
        }
        var anyAllow = options.FirstOrDefault(o => o.OptionId.Contains("allow", StringComparison.OrdinalIgnoreCase));
        if (anyAllow is not null) return anyAllow.OptionId;
        return options.FirstOrDefault()?.OptionId ?? "allow_once";
    }

    private static JsonNode BuildOutcome(string optionId, IReadOnlyList<ApprovalOption> options)
    {
        var picked = optionId;
        if (!options.Any(o => o.OptionId == optionId))
            picked = options.FirstOrDefault()?.OptionId ?? "reject_once";
        return new JsonObject
        {
            ["outcome"] = new JsonObject
            {
                ["outcome"] = "selected",
                ["optionId"] = picked,
            }
        };
    }
}

/// <summary>
/// Snapshot of a single in-flight prompt/turn for a session, captured by
/// <see cref="AcpSessionManager.PromptAsync"/>. Surfaced via
/// <see cref="AcpSessionManager.IsTurnInFlight"/> for the GET /state endpoint.
/// </summary>
public readonly record struct InFlightEntry(string? Requester, DateTimeOffset StartedAt);
