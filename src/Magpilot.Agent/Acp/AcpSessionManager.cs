using System.Collections.Concurrent;
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
    private readonly AcpFlavorPool _pool;
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

    private static readonly string SessionStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    private static string EventsPath(string sessionId) =>
        Path.Combine(SessionStateRoot, sessionId, "events.jsonl");

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
    /// Open (pending / in-progress) tool-call ids per session. A turn that is
    /// waiting on a tool it invoked -- a long shell command, a slow MCP call --
    /// is silent on the ACP stream between the tool's <c>tool_call</c> (pending)
    /// and <c>tool_call_update</c> (completed) updates, but it is NOT wedged. The
    /// watchdog consults this so it never mistakes a legitimately long tool for a
    /// hung model. Cleared when the turn ends.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _openToolCalls = new();

    public AcpSessionManager(AcpFlavorPool pool, Magpilot.Agent.Sessions.YoloRegistry yolo, ILogger<AcpSessionManager> logger)
    {
        _pool = pool;
        _yolo = yolo;
        _logger = logger;
        _pool.OnSessionUpdate += HandleUpdate;
        _pool.OnRequest += HandleRequestAsync;
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
        if (!_inFlight.ContainsKey(sessionId)) return;
        var tcs = _turnDone.GetOrAdd(sessionId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    /// <summary>
    /// Resolve the ACP client owning <paramref name="sessionId"/>. If we
    /// don't know which client (e.g. session predates this agent process),
    /// fall back to the default-flavor client and remember the mapping.
    /// </summary>
    private async Task<AcpClient> ClientForAsync(string sessionId, CancellationToken ct)
    {
        if (_sessionClient.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }
        var fallback = await _pool.AcquireAsync(AcpFlavor.Default, ct);
        _sessionFlavor.TryAdd(sessionId, AcpFlavor.Default);
        return _sessionClient.GetOrAdd(sessionId, fallback);
    }

    public async Task<string> NewSessionAsync(string cwd, AcpFlavor flavor, CancellationToken ct)
    {
        var client = await _pool.AcquireAsync(flavor, ct);
        var res = await client.CallAsync("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, ct);
        var sid = res?["sessionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("session/new returned no sessionId");
        _sessionClient[sid] = client;
        _sessionFlavor[sid] = flavor;
        _freshness.RecordServed(sid, EventsPath(sid));
        MarkOurPid(sid, client.ProcessId);
        _logger.LogInformation("New ACP session {SessionId} cwd={Cwd} flavor={Flavor}", sid, cwd, flavor.Key);
        return sid;
    }

    public async Task LoadSessionAsync(string sessionId, string cwd, AcpFlavor flavor, CancellationToken ct)
    {
        var client = await _pool.AcquireAsync(flavor, ct);
        await client.CallAsync("session/load", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, ct, timeoutSec: 300);
        _sessionClient[sessionId] = client;
        _sessionFlavor[sessionId] = flavor;
        _freshness.RecordServed(sessionId, EventsPath(sessionId));
        MarkOurPid(sessionId, client.ProcessId);
    }

    /// <summary>
    /// Clear a stale resume by recycling the multiplexing ACP child that holds
    /// <paramref name="sessionId"/>: kill it (the only way copilot releases a
    /// loaded session -- <c>session/close</c> is unimplemented and
    /// <c>session/load</c> won't re-read disk for an already-loaded session),
    /// spawn a fresh child, and reload the session from disk so current state is
    /// served. Every OTHER session the child multiplexed is dropped from our
    /// routing and its stale lock reaped, so it reads as Dormant and reloads from
    /// disk on next adopt. Refuses (<see cref="RecycleOutcome.Busy"/>) while any
    /// co-hosted session has a turn in flight, so a live turn is never killed.
    /// <paramref name="cwdResolver"/> supplies the reload cwd; the registry backs
    /// it with the session scanner.
    /// </summary>
    public async Task<RecycleOutcome> RecycleForStaleAsync(string sessionId, Func<string, string?> cwdResolver, CancellationToken ct)
    {
        if (!_sessionClient.TryGetValue(sessionId, out var target))
            return RecycleOutcome.NotLoaded;

        var coHosted = _sessionClient
            .Where(kv => ReferenceEquals(kv.Value, target))
            .Select(kv => kv.Key)
            .ToList();

        var busy = coHosted.Where(_inFlight.ContainsKey).ToList();
        if (busy.Count > 0)
        {
            _logger.LogWarning(
                "Stale recycle for {Sid} deferred: {Count} co-hosted session(s) have a turn in flight ({Busy})",
                sessionId, busy.Count, string.Join(", ", busy));
            return RecycleOutcome.Busy;
        }

        _logger.LogWarning(
            "Recycling multiplexing ACP child to clear stale resume of {Sid}; {Count} co-hosted session(s) will reload from disk on next use",
            sessionId, coHosted.Count);

        await _pool.RecycleAsync(AcpFlavor.Default, ct);

        // Drop routing for every session the killed child held and reap its now-
        // dead locks so a rescan sees Dormant (free to reload), not Locked.
        foreach (var s in coHosted)
        {
            _sessionClient.TryRemove(s, out _);
            _freshness.Forget(s);
            _ourSessionPids.TryRemove(s, out _);
            try { Magpilot.Agent.Sessions.SessionLocks.ReapDead(Path.Combine(SessionStateRoot, s)); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reaping locks for {Sid} during recycle threw", s); }
        }

        // Eagerly reload the session that triggered this so the resume serves
        // current state; bystanders self-heal via the Dormant path on next use.
        var cwd = cwdResolver(sessionId) ?? Environment.CurrentDirectory;
        await LoadSessionAsync(sessionId, cwd, AcpFlavor.Default, ct);
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

            // Unblock the caller first. PromptAsync also emits TurnComplete(error)
            // once the recycle faults its session/prompt call, but an explicit
            // error lets a voice caller speak a failure instead of ending silent.
            Publish(sid, new ErrorEvent("The assistant stalled and was reset. Please try again."));

            try
            {
                await RecycleForStalledTurnAsync(sid, client, cwdResolver, ct);
                recovered++;
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
    private async Task RecycleForStalledTurnAsync(string sessionId, AcpClient target, Func<string, string?> cwdResolver, CancellationToken ct)
    {
        var flavor = _sessionFlavor.TryGetValue(sessionId, out var f) ? f : AcpFlavor.Default;

        var coHosted = _sessionClient
            .Where(kv => ReferenceEquals(kv.Value, target))
            .Select(kv => kv.Key)
            .ToList();

        if (flavor.MultiplexesSessions)
            await _pool.RecycleAsync(flavor, ct);
        else
            await target.DisposeAsync(); // per-session child; just kill it

        // Drop routing for every session the killed child held and reap its now-
        // dead locks so a rescan sees Dormant (free to reload), not Locked. Keep
        // each session's flavor mapping so a bystander reloads on its own flavor.
        foreach (var s in coHosted)
        {
            _sessionClient.TryRemove(s, out _);
            _freshness.Forget(s);
            _ourSessionPids.TryRemove(s, out _);
            _lastEventAt.TryRemove(s, out _);
            _openToolCalls.TryRemove(s, out _);
            try { Magpilot.Agent.Sessions.SessionLocks.ReapDead(Path.Combine(SessionStateRoot, s)); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reaping locks for {Sid} during stalled-turn recycle threw", s); }
        }

        var cwd = cwdResolver(sessionId) ?? Environment.CurrentDirectory;
        await LoadSessionAsync(sessionId, cwd, flavor, ct);
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
        var dir = Path.Combine(SessionStateRoot, sessionId);
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
        var dir = Path.Combine(SessionStateRoot, sessionId);
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

    public async Task PromptAsync(string sessionId, string text, CancellationToken ct, string? requester = null, string? source = null)
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
        var stopReason = "end_turn";
        _inFlight[sessionId] = new InFlightEntry(requester, DateTimeOffset.UtcNow);
        try
        {
            var client = await ClientForAsync(sessionId, ct);
            var resp = await client.CallAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = promptText },
                },
            }, ct, timeoutSec: 600);
            stopReason = resp?["stopReason"]?.GetValue<string>() ?? stopReason;
        }
        catch (Exception ex)
        {
            stopReason = "error";
            _logger.LogWarning(ex, "session/prompt failed for {Sid}", sessionId);
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
            _openToolCalls.TryRemove(sessionId, out _);
            // Wake anyone waiting for the turn to finish.
            if (_turnDone.TryRemove(sessionId, out var tcs))
                tcs.TrySetResult();
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
    /// <c>session/close</c> over JSON-RPC (which copilot --acp
    /// actually rejects with -32601 today, but we issue it anyway in
    /// case a future copilot adds support) and then sweeps the
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
    public async Task CloseAsync(string sessionId, string? sessionsRoot, CancellationToken ct)
    {
        // Capture the client's PID BEFORE the CloseAsync call, since
        // we need it to identify which lock file to delete and the
        // call might null-out our cache mapping on success.
        AcpClient? client = null;
        _sessionClient.TryGetValue(sessionId, out client);
        var clientPid = client?.ProcessId;

        try
        {
            if (client is not null)
                await client.CallAsync("session/close", new JsonObject { ["sessionId"] = sessionId }, ct, timeoutSec: 30);
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
            _sessionClient.TryRemove(sessionId, out _);
            _freshness.Forget(sessionId);
            _ourSessionPids.TryRemove(sessionId, out _);
            _lastEventAt.TryRemove(sessionId, out _);
            _sessionFlavor.TryRemove(sessionId, out _);
            _openToolCalls.TryRemove(sessionId, out _);
        }
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

    private void HandleUpdate(string sessionId, JsonNode? update)
    {
        if (update is null) return;
        // Any update from the child means this session's turn is making progress.
        // Record it so the turn watchdog can distinguish a live-but-slow turn from
        // a wedged one that emits nothing.
        _lastEventAt[sessionId] = DateTimeOffset.UtcNow;
        var kind = update["sessionUpdate"]?.GetValue<string>();
        // Track open tool calls so the watchdog can tell a turn waiting on a long
        // tool (silent between the tool's pending and completed updates) apart from
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
