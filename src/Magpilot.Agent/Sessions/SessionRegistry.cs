using System.Collections.Concurrent;
using System.Diagnostics;
using Magpilot.Agent.Acp;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Bridges on-disk Copilot CLI sessions with the live ACP process.
/// Maintains the set of "owned" sessionIds (those currently loaded into
/// our ACP child) and orchestrates adoption of locked sessions.
/// </summary>
public sealed class SessionRegistry
{
    private readonly AcpSessionManager _acp;
    private readonly SessionScanner _scanner;
    private readonly HostOwnership _hostOwnership;
    private readonly YoloRegistry _yolo;
    private readonly ILogger<SessionRegistry> _logger;
    private readonly ConcurrentDictionary<string, byte> _owned = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new();
    // Current-process copy of the flavor captured at host acquisition. The
    // persisted HostOwnership entry covers normal live launchers across agent
    // restarts; this copy also covers synthetic/dead host PIDs whose ownership
    // entry is pruned before a failed handback can be retried.
    private readonly ConcurrentDictionary<string, HostSessionFlavor> _handoffFlavor = new();

    // Opt-in: when set, a resume detected as stale recycles the multiplexing ACP
    // child and reloads current disk state instead of only warning. Default off
    // (enable with MAGPILOT_STALE_RECYCLE=1|true|yes|on) so it is turned on
    // deliberately per host.
    private readonly bool _recycleOnStale =
        (Environment.GetEnvironmentVariable("MAGPILOT_STALE_RECYCLE") ?? "").Trim().ToLowerInvariant()
            is "1" or "true" or "yes" or "on";

    public SessionRegistry(AcpSessionManager acp, SessionScanner scanner, HostOwnership hostOwnership, YoloRegistry yolo, ILogger<SessionRegistry> logger)
    {
        _acp = acp;
        _scanner = scanner;
        _hostOwnership = hostOwnership;
        _yolo = yolo;
        _logger = logger;
    }

    public IReadOnlySet<string> Owned => _owned.Keys
        .Where(IsAgentOwned)
        .ToHashSet();

    private bool IsAgentOwned(string sessionId) =>
        _owned.ContainsKey(sessionId) &&
        _acp.IsAttached(sessionId) &&
        !_acp.IsQuarantined(sessionId);

    public IReadOnlyList<SessionInfo> List() =>
        _scanner.Enumerate(Owned).Select(s => s with { Yolo = _yolo.IsEnabled(s.Id) }).ToList();

    public SessionInfo? Get(string id) => WithYolo(_scanner.Get(id, Owned));

    // SessionInfo is on-disk-derived; Yolo lives in-memory in YoloRegistry.
    // Decorate at the read boundary so callers (HTTP, SPA) see a single
    // consistent record without scattering YoloRegistry lookups across the
    // codebase.
    private SessionInfo? WithYolo(SessionInfo? info) =>
        info is null ? null : info with { Yolo = _yolo.IsEnabled(info.Id) };

    // cwd for a session, from its on-disk workspace.yaml; backs the recycle
    // reload path so AcpSessionManager stays free of the scanner.
    internal string? CwdFor(string sessionId) => _scanner.Get(sessionId, Owned)?.Cwd;

    private async Task<SemaphoreSlim> AcquireLifecycleGateAsync(string sessionId, CancellationToken ct)
    {
        var gate = _lifecycleGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return gate;
    }

    public async Task<SessionInfo> CreateAsync(string? cwd, bool useAgency, CancellationToken ct, string? name = null, string? model = null, string? reasoningEffort = null, string[]? disableMcpServers = null)
    {
        cwd ??= Environment.CurrentDirectory;
        var flavor = AcpFlavor.Resolve(useAgency, model, reasoningEffort, disableMcpServers);
        // AcpSessionManager invokes onAttached only after the complete requested
        // configuration has been applied and verified. A failed configure may
        // leave a quarantined route for retry, but it is not advertised as Owned.
        var sid = await _acp.NewSessionAsync(cwd, flavor, ct, onAttached: id => _owned.TryAdd(id, 0));

        // Stamp a friendly name into workspace.yaml if the caller supplied
        // one. Copilot CLI auto-derives a summary from the first user
        // message, which is awful for sessions whose first message is a
        // long prompt template (e.g. cron heartbeats). A user-set name
        // overrides that.
        if (!string.IsNullOrWhiteSpace(name))
            TryWriteWorkspaceField(sid, "name", name, "summary", name);

        return WithYolo(_scanner.Get(sid, Owned))
            ?? new SessionInfo(sid, SessionState.Owned, cwd, null, null, name, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static void TryWriteWorkspaceField(string sessionId, params string[] keyValuePairs)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".copilot", "session-state", sessionId);
            var path = Path.Combine(dir, "workspace.yaml");
            // workspace.yaml is written by the Copilot CLI shortly after
            // session/new returns. Wait briefly for it to appear.
            for (var i = 0; i < 30 && !File.Exists(path); i++)
                Thread.Sleep(100);
            if (!File.Exists(path)) return;

            var lines = File.ReadAllLines(path).ToList();
            // Build a fresh literal-quoted value for each key. Drop any
            // existing entry first (including multi-line literal blocks).
            for (var i = 0; i < keyValuePairs.Length; i += 2)
            {
                var k = keyValuePairs[i];
                var v = keyValuePairs[i + 1];
                // Remove existing key (and any continuation lines if it was
                // a literal block scalar).
                for (var j = 0; j < lines.Count; j++)
                {
                    if (lines[j].StartsWith(k + ":", StringComparison.Ordinal))
                    {
                        lines.RemoveAt(j);
                        // Strip continuation lines (indented) from a |- block.
                        while (j < lines.Count && (lines[j].StartsWith("  ") || string.IsNullOrWhiteSpace(lines[j])))
                            lines.RemoveAt(j);
                        break;
                    }
                }
                // Always quote so the line-based parser reads cleanly.
                lines.Add($"{k}: \"{v.Replace("\"", "\\\"")}\"");
            }
            File.WriteAllLines(path, lines);
        }
        catch { /* best-effort -- session is still usable without the name */ }
    }

    /// <summary>
    /// Adopt: if the session is held by another process, kill it (force=true required),
    /// then session/load it into our ACP child.
    /// Callers may re-supply model/reasoning/tool-scope settings to restore the
    /// same process scope and re-apply persisted ACP session configuration after
    /// the load. Process flavor routing itself is not persisted on disk.
    /// </summary>
    public async Task<SessionInfo> AdoptAsync(string sessionId, bool force, CancellationToken ct, string? model = null, string? reasoningEffort = null, string[]? disableMcpServers = null)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            return await AdoptCoreAsync(sessionId, force, ct, model, reasoningEffort, disableMcpServers);
        }
        finally { gate.Release(); }
    }

    private async Task<SessionInfo> AdoptCoreAsync(string sessionId, bool force, CancellationToken ct, string? model, string? reasoningEffort, string[]? disableMcpServers)
    {
        var info = _scanner.Get(sessionId, Owned)
            ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
        if (_hostOwnership.TryGet(sessionId, out var host))
        {
            throw new InvalidOperationException(
                $"Session is held by magpilot launcher PID {host.HostPid}; release it from the host before adopting.");
        }
        var requestedFlavor = AcpFlavor.Resolve(
            useAgency: false,
            model,
            reasoningEffort,
            disableMcpServers);

        // Contention guard. Advisory inuse.<pid>.lock files let two live
        // processes attach to the same session; a resume can then be served
        // from one process's frozen in-memory snapshot while another advances
        // the session on disk. copilot cannot be forced to reload an
        // already-loaded session, so the least we can do is make the divergence
        // loud.
        var liveHolders = SessionLocks.Live(SessionLocks.Inspect(Path.Combine(_scanner.Root, sessionId)));
        if (liveHolders.Count > 1)
        {
            _logger.LogWarning(
                "Session {Sid} has {Count} live lock holders (pids: {Pids}) at adopt -- concurrent " +
                "writers; served context may be stale relative to disk",
                sessionId, liveHolders.Count, string.Join(", ", liveHolders.Select(h => h.Pid)));
        }

        // A prior load may have attached the session but failed while applying
        // its requested configuration. The child cannot load the same session a
        // second time, so retry against the quarantined route in place. It only
        // becomes registry-Owned after the complete tuple verifies.
        if (_acp.IsAttached(sessionId) &&
            _acp.IsQuarantined(sessionId) &&
            !_hostOwnership.TryGet(sessionId, out _))
        {
            await _acp.ApplyOwnedConfigurationAsync(
                sessionId,
                requestedFlavor,
                processScopeSpecified: disableMcpServers is not null,
                ct);
            _owned.TryAdd(sessionId, 0);
            return CompleteAdopt(sessionId, WithYolo(_scanner.Get(sessionId, Owned) ?? info)!);
        }

        // Recycling a shared child invalidates every route it hosted. The stale
        // registry marker is intentionally enough to recognize one of our
        // invalidated bystanders even if its old live-looking lock has not yet
        // disappeared; reload it from disk instead of treating that lock as an
        // unrelated process that requires force.
        if (_owned.ContainsKey(sessionId) && !_acp.IsAttached(sessionId))
        {
            var retainedFlavor = _acp.EffectiveFlavor(sessionId);
            var reloadFlavor = retainedFlavor is null
                ? requestedFlavor
                : AcpFlavor.Resolve(
                    Describe(retainedFlavor)!.UseAgency,
                    model ?? retainedFlavor.Model,
                    reasoningEffort ?? retainedFlavor.ReasoningEffort,
                    disableMcpServers ?? retainedFlavor.DisabledMcpServers);
            _logger.LogWarning(
                "Session {Sid} was invalidated with its co-hosted ACP child; re-attaching it from disk",
                sessionId);
            await _acp.ReloadFromDiskAsync(
                sessionId,
                info.Cwd ?? Environment.CurrentDirectory,
                reloadFlavor,
                ct,
                onAttached: id => _owned.TryAdd(id, 0));
            return CompleteAdopt(
                sessionId,
                WithYolo(_scanner.Get(sessionId, Owned)) ?? info with { State = SessionState.Owned });
        }

        // A cancelled/timed-out session/load, or a normal detach, can leave the
        // session resident in one of our children without a published route.
        // Its live lock is ours, not a foreign owner that should require force.
        // Recycle the holder and retry from disk before the generic Locked path.
        if (!_acp.IsAttached(sessionId) && _acp.IsResident(sessionId))
        {
            var retainedFlavor = _acp.EffectiveFlavor(sessionId);
            var reloadFlavor = retainedFlavor is null
                ? requestedFlavor
                : AcpFlavor.Resolve(
                    Describe(retainedFlavor)!.UseAgency,
                    model ?? retainedFlavor.Model,
                    reasoningEffort ?? retainedFlavor.ReasoningEffort,
                    disableMcpServers ?? retainedFlavor.DisabledMcpServers);
            await _acp.ReloadFromDiskAsync(
                sessionId,
                info.Cwd ?? Environment.CurrentDirectory,
                reloadFlavor,
                ct,
                onAttached: id => _owned.TryAdd(id, 0));
            return CompleteAdopt(
                sessionId,
                WithYolo(_scanner.Get(sessionId, Owned)) ?? info with { State = SessionState.Owned });
        }

        var eventsPath = Path.Combine(_scanner.Root, sessionId, "events.jsonl");
        if (!_acp.IsAttached(sessionId) && !File.Exists(eventsPath))
        {
            throw new SessionNotLoadableException(
                sessionId,
                $"Session {sessionId} has no events.jsonl and is not resident in a live ACP child; it cannot be loaded.");
        }

        if (info.State == SessionState.Owned)
        {
            // Detect a stale resume: another process advanced this session on disk
            // past what our child loaded. copilot cannot reload a session in place,
            // so the only way to serve current state is to recycle the child that
            // holds it (kill + respawn + reload from disk). Opt-in via
            // MAGPILOT_STALE_RECYCLE; otherwise surface it loudly.
            if (_acp.MayBeStale(sessionId))
            {
                if (_recycleOnStale)
                {
                    var outcome = await _acp.RecycleForStaleAsync(sessionId, CwdFor, ct);
                    switch (outcome)
                    {
                        case RecycleOutcome.Recycled:
                            _logger.LogWarning(
                                "Session {Sid} resume was stale; recycled the ACP child and reloaded current state from disk",
                                sessionId);
                            if (_acp.HasForeignLiveHolder(sessionId))
                                _logger.LogWarning(
                                    "Session {Sid} still has a live foreign holder after recycle; it may go stale again while that process keeps writing",
                                    sessionId);
                            break;
                        case RecycleOutcome.Busy:
                            _logger.LogWarning(
                                "Session {Sid} resume is stale but a co-hosted turn is in flight; served context stays behind until it is idle",
                                sessionId);
                            break;
                        case RecycleOutcome.NotLoaded:
                            _acp.ResyncWatermark(sessionId);
                            break;
                    }
                }

                else
                {
                    _logger.LogWarning(
                        "Session {Sid} resume is stale: another process advanced it on disk past our loaded copy, so served " +
                        "context is behind. Set MAGPILOT_STALE_RECYCLE=true to auto-recycle, or use the owning process for current context.",
                        sessionId);
                }
            }
            else
                _acp.ResyncWatermark(sessionId); // absorb our child's async post-turn flush

            // An Owned session must still honor an adopt request's config. ACP
            // session/load is not idempotent, so apply and verify against the
            // latest configOptions state captured from setup/set responses and
            // config_option_update notifications. Process-scoped MCP changes
            // cannot be made in place and fail explicitly.
            await _acp.ApplyOwnedConfigurationAsync(
                sessionId,
                requestedFlavor,
                processScopeSpecified: disableMcpServers is not null,
                ct);
            return CompleteAdopt(sessionId, WithYolo(_scanner.Get(sessionId, Owned) ?? info)!);
        }

        if (info.State == SessionState.Locked)
        {
            if (!force) throw new InvalidOperationException("Session is held by another process; pass force=true to take over.");
            if (info.OwnerPid is int pid)
            {
                try
                {
                    var p = Process.GetProcessById(pid);
                    _logger.LogWarning("Killing PID {Pid} to adopt session {Sid}", pid, sessionId);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not kill PID {Pid}", pid); }
            }
            // Wait briefly for the lock file to vanish
            for (var i = 0; i < 20; i++)
            {
                var refreshed = _scanner.Get(sessionId, Owned);
                if (refreshed?.State == SessionState.Dormant) break;
                await Task.Delay(100, ct);
            }
        }

        var cwd = info.Cwd ?? Environment.CurrentDirectory;
        // Load using the requested process scope, then apply model/reasoning as
        // per-session ACP config. A bootstrap re-supplies process-scoped MCP
        // exclusions on every boot because child flavor routing is not persisted.
        // Ownership is claimed only after configuration verifies. A failure keeps
        // a quarantined attached route that the early retry path above can
        // configure without issuing a second session/load.
        await _acp.ReloadFromDiskAsync(sessionId, cwd, requestedFlavor, ct, onAttached: id => _owned.TryAdd(id, 0));
        return CompleteAdopt(
            sessionId,
            WithYolo(_scanner.Get(sessionId, Owned)) ?? info with { State = SessionState.Owned });
    }

    private SessionInfo CompleteAdopt(string sessionId, SessionInfo info)
    {
        // A normal adopt after the old host exited supersedes that handoff.
        // Consume its durable record so a delayed /release cannot later repin
        // the old tuple or remove this newly restored ownership.
        _hostOwnership.Clear(sessionId);
        _handoffFlavor.TryRemove(sessionId, out _);
        return info;
    }

    public async Task<AcpFlavor?> DetachAsync(string sessionId, CancellationToken ct, bool force = false)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            return await DetachCoreAsync(sessionId, ct, force);
        }
        finally { gate.Release(); }
    }

    private async Task<AcpFlavor?> DetachCoreAsync(string sessionId, CancellationToken ct, bool force)
    {
        if (!_acp.IsAttached(sessionId))
        {
            _owned.TryRemove(sessionId, out _);
            return null;
        }

        var flavor = force
            ? await _acp.ForceDetachAsync(sessionId, ct)
            : await _acp.CloseAsync(sessionId, _scanner.Root, ct);
        _owned.TryRemove(sessionId, out _);
        return flavor;
    }

    /// <summary>
    /// Compute the rich ownership + activity view of a session for the
    /// magpilot launcher or SPA. Cheap: filesystem stat + a few
    /// in-memory lookups + an events.jsonl tail read.
    /// </summary>
    public SessionStateInfo? GetState(string sessionId)
    {
        var info = WithYolo(_scanner.Get(sessionId, Owned));
        if (info is null) return null;

        SessionOwner owner;
        int? hostPid = null;
        if (_hostOwnership.TryGet(sessionId, out var hostEntry))
        {
            owner = SessionOwner.Host;
            hostPid = hostEntry.HostPid;
        }
        else if (IsAgentOwned(sessionId))
        {
            owner = SessionOwner.Agent;
        }
        else if (info.OwnerPid is int ownerPid && IsAlive(ownerPid))
        {
            owner = SessionOwner.External;
        }
        else
        {
            owner = SessionOwner.None;
        }

        SessionActivity activity;
        InFlightInfo? inFlight = null;
        if (_acp.IsTurnInFlight(sessionId, out var entry))
        {
            activity = SessionActivity.InFlight;
            inFlight = new InFlightInfo(
                Driver: entry.Requester,
                StartedAtMs: entry.StartedAt.ToUnixTimeMilliseconds(),
                Preview: null);
        }
        else
        {
            activity = SessionActivity.Idle;
        }

        var lastEvent = TryReadLastEvent(sessionId);

        return new SessionStateInfo(info, owner, hostPid, activity, inFlight, lastEvent);
    }

    /// <summary>
    /// Hand off a session to a magpilot launcher. Atomic combined op:
    /// waits for any in-flight turn to reach a clean boundary (or aborts
    /// it if <paramref name="force"/> is true), drops our agent-side
    /// ownership, and records the host as the new owner.
    /// </summary>
    public async Task<SessionStateInfo> AcquireForHostAsync(string sessionId, int hostPid, bool force, CancellationToken ct)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        var draining = false;
        try
        {
            await _acp.BeginSessionDrainAsync(sessionId, ct);
            draining = true;
            return await AcquireForHostCoreAsync(sessionId, hostPid, force, ct);
        }
        finally
        {
            if (draining)
                await _acp.EndSessionDrainAsync(sessionId);
            gate.Release();
        }
    }

    private async Task<SessionStateInfo> AcquireForHostCoreAsync(string sessionId, int hostPid, bool force, CancellationToken ct)
    {
        var scanned = _scanner.Get(sessionId, Owned);
        if (scanned is null)
            throw new FileNotFoundException($"Session {sessionId} not on disk");
        if (_hostOwnership.TryGet(sessionId, out var liveHost))
        {
            if (liveHost.HostPid == hostPid)
                return GetState(sessionId)!;
            throw new InvalidOperationException(
                $"Session is already held by host PID {liveHost.HostPid}; it must release before host PID {hostPid} can acquire it.");
        }
        if (!_acp.IsAttached(sessionId) &&
            scanned.State == SessionState.Locked &&
            scanned.OwnerPid is int externalPid &&
            IsAlive(externalPid) &&
            externalPid != hostPid)
        {
            if (!force)
            {
                throw new InvalidOperationException(
                    $"Session is held by external PID {externalPid}; pass force=true to evict it before acquiring.");
            }

            var evicted = _acp.EvictForeignLiveHolders(sessionId);
            if (_acp.HasForeignLiveHolder(sessionId))
            {
                throw new InvalidOperationException(
                    $"Could not evict the external holder of session {sessionId}; host acquisition was not recorded.");
            }
            _logger.LogWarning(
                "AcquireForHost force-evicted {Count} external holder(s) on {Sid}: {Pids}",
                evicted.Count,
                sessionId,
                string.Join(", ", evicted));
        }

        // If we currently own the session, gracefully release it.
        HostSessionFlavor? flavor = null;
        if (_acp.IsAttached(sessionId))
        {
            _handoffFlavor.TryGetValue(sessionId, out var pendingFlavor);
            var preservePendingFlavor =
                _acp.IsQuarantined(sessionId) &&
                pendingFlavor is not null;
            // Capture what the session is actually running under BEFORE detaching:
            // process-scoped tool surface plus the model/reasoning the child last
            // confirmed. Handback restores exactly this, so a launcher round-trip
            // no longer silently demotes a pinned session to the default flavor.
            var forceDetach = false;
            if (_acp.IsTurnInFlight(sessionId, out _))
            {
                if (force)
                {
                    _logger.LogInformation("AcquireForHost (force): cancelling in-flight turn on {Sid}", sessionId);
                    try { await _acp.CancelAsync(sessionId, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "session/cancel failed during force-acquire for {Sid}", sessionId); }
                    // Give the turn ~2s to finalize (it should emit TurnComplete).
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    grace.CancelAfter(TimeSpan.FromSeconds(2));
                    try { await _acp.WaitForTurnBoundaryAsync(sessionId, grace.Token); }
                    catch (OperationCanceledException) { /* stop waiting; we'll detach anyway */ }
                    forceDetach = _acp.IsTurnInFlight(sessionId, out _);
                }
                else
                {
                    _logger.LogInformation("AcquireForHost (polite): waiting for in-flight turn on {Sid}", sessionId);
                    await _acp.WaitForTurnBoundaryAsync(sessionId, ct);
                }
            }
            var detachedFlavor = Describe(await DetachCoreAsync(sessionId, ct, forceDetach));
            flavor = preservePendingFlavor ? pendingFlavor : detachedFlavor;
        }
        else
        {
            if (_handoffFlavor.TryGetValue(sessionId, out var pendingFlavor))
                flavor = pendingFlavor;
            else if (_hostOwnership.TryGetRecorded(sessionId, out var existingHost))
                flavor = existingHost.Flavor;
            else
                flavor = Describe(_acp.EffectiveFlavor(sessionId));
            _owned.TryRemove(sessionId, out _);
        }

        if (flavor is not null)
            _handoffFlavor[sessionId] = flavor;
        else
            _handoffFlavor.TryRemove(sessionId, out _);
        _hostOwnership.Set(sessionId, hostPid, flavor);

        // Drop the in-memory yolo bit so the terminal user (now at a
        // real keyboard with a TTY) gets the standard interactive
        // approval prompts rather than silent auto-approve they didn't
        // opt into. The wrapper can flip yolo back on later via the
        // agent's HTTP API if desired.
        _yolo.Clear(sessionId);

        // Refresh state for the response (now reflecting host ownership).
        return GetState(sessionId)!;
    }

    /// <summary>
    /// Hand a host-owned session back to the agent. In the cooperative case the
    /// launcher has already shut its child copilot down; we clear the host marker
    /// and re-load the session into our ACP child. If a live foreign copilot still
    /// holds the session, behaviour depends on <paramref name="force"/>:
    /// <list type="bullet">
    ///   <item><c>force=false</c> (graceful): do NOT kill it. Decline to adopt and
    ///     retain Host ownership and the recorded flavor so the caller can retry
    ///     or offer a forceful takeover. This preserves the terminal's cooperative
    ///     "resume here" prompt.</item>
    ///   <item><c>force=true</c>: evict the live foreign copilot first, then adopt.
    ///     This ends the terminal session outright, so it is only ever driven by an
    ///     explicit user "Force take over".</item>
    /// </list>
    /// Adopting while a foreign copilot still writes would split-brain one session
    /// across two drivers (the "garbled then stalled" failure), so we never adopt
    /// while a live foreign holder remains.
    /// </summary>
    public async Task<SessionStateInfo> ReleaseFromHostAsync(string sessionId, int hostPid, bool force, CancellationToken ct)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            return await ReleaseFromHostCoreAsync(sessionId, hostPid, force, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<SessionStateInfo> ReleaseFromHostCoreAsync(string sessionId, int hostPid, bool force, CancellationToken ct)
    {
        var info = _scanner.Get(sessionId, Owned)
            ?? throw new FileNotFoundException($"Session {sessionId} not on disk");

        HostSessionFlavor? recordedFlavor = null;
        var hasReleaseRecord = false;
        if (_hostOwnership.TryGetRecorded(sessionId, out var entry))
        {
            if (entry.HostPid != hostPid)
                throw new InvalidOperationException(
                    $"Session is held by host PID {entry.HostPid}, not {hostPid}; cannot release on its behalf.");
            recordedFlavor = entry.Flavor;
            hasReleaseRecord = true;
        }
        if (_handoffFlavor.TryGetValue(sessionId, out var pendingFlavor))
        {
            recordedFlavor = pendingFlavor;
            hasReleaseRecord = true;
        }
        if (!hasReleaseRecord)
        {
            _logger.LogInformation(
                "Ignoring stale ReleaseFromHost for {Sid} from PID {Pid}; no matching handoff remains",
                sessionId,
                hostPid);
            return GetState(sessionId)!;
        }

        // Only a FORCEFUL release evicts a still-live foreign holder. In the
        // cooperative handoff the launcher has already torn its copilot down, so
        // there is nothing to evict and this is a no-op either way. But if the
        // launcher never heard the release_requested knock (deaf/dead subscription)
        // or was mid-turn, its interactive copilot is still attached and appending
        // to events.jsonl. Killing it is destructive -- it ends the terminal
        // session with no cooperative "resume here" prompt -- so we only do it on
        // an explicit force (the SPA's "Force take over"). A graceful release
        // leaves the terminal untouched and simply declines to adopt below.
        if (force)
        {
            var evicted = _acp.EvictForeignLiveHolders(sessionId);
            if (evicted.Count > 0)
                _logger.LogWarning("ReleaseFromHost force-evicted {Count} live foreign holder(s) on {Sid}: {Pids}",
                    evicted.Count, sessionId, string.Join(", ", evicted));
        }

        // Never adopt while a live foreign copilot still holds the session: two
        // live drivers on one events.jsonl is the split-brain. On a graceful
        // release this is the normal "launcher hasn't let go yet" outcome (the
        // caller re-raises the takeover choice); on a forceful release it only
        // happens if the eviction couldn't complete (e.g. no permission to kill).
        // Either way, retain the host record (and its saved flavor) so a later
        // release can retry without losing the configuration to restore.
        if (_acp.HasForeignLiveHolder(sessionId))
        {
            _logger.LogInformation(
                "ReleaseFromHost: a live foreign holder remains on {Sid}; not adopting (force={Force})",
                sessionId, force);
            return GetState(sessionId)!;
        }

        // Re-load the session into our ACP child. The host's interactive copilot
        // appended to events.jsonl while it was driving, and our own child still
        // has the pre-detach snapshot in memory (copilot implements neither
        // session/close nor a disk re-read for an already-loaded session), so the
        // holding child is recycled first -- otherwise session/load either answers
        // "already loaded" or serves the frozen copy. The session comes back on
        // the flavor it left on: same process/tool scope, same model, same
        // reasoning. The ACP manager may retain a quarantined route if
        // configuration fails, but registry ownership is not restored until the
        // complete tuple verifies.
        var cwd = info.Cwd ?? Environment.CurrentDirectory;
        var flavor = ResolveFlavor(recordedFlavor) ?? _acp.EffectiveFlavor(sessionId) ?? AcpFlavor.Default;
        try
        {
            if (_acp.IsAttached(sessionId))
            {
                await _acp.ApplyOwnedConfigurationAsync(
                    sessionId,
                    flavor,
                    processScopeSpecified: true,
                    ct);
            }
            else
            {
                await _acp.ReloadFromDiskAsync(sessionId, cwd, flavor, ct);
            }

            _owned.TryAdd(sessionId, 0);
            _hostOwnership.Clear(sessionId);
            _handoffFlavor.TryRemove(sessionId, out _);
        }
        catch (Exception ex)
        {
            _owned.TryRemove(sessionId, out _);
            // Keep host ownership and its recorded flavor. Either attach never
            // happened, or the manager retained a quarantined route that the next
            // release attempt can configure in place. In neither case do we
            // advertise the session as agent-owned.
            _logger.LogWarning(ex, "Re-attaching {Sid} during ReleaseFromHost failed (flavor={Flavor})", sessionId, flavor.Key);
        }

        return GetState(sessionId)!;
    }

    /// <summary>
    /// Snapshot an <see cref="AcpFlavor"/> as the plain record the ownership map
    /// persists, so a handback can rebuild it after an agent restart.
    /// </summary>
    private static HostSessionFlavor? Describe(AcpFlavor? flavor) =>
        flavor is null
            ? null
            : new HostSessionFlavor(
                UseAgency: string.Equals(flavor.Key, AcpFlavor.Agency.Key, StringComparison.Ordinal)
                    || flavor.Key.StartsWith(AcpFlavor.Agency.Key + ":", StringComparison.Ordinal),
                Model: flavor.Model,
                ReasoningEffort: flavor.ReasoningEffort,
                DisabledMcpServers: flavor.DisabledMcpServers?.ToArray());

    private static AcpFlavor? ResolveFlavor(HostSessionFlavor? recorded) =>
        recorded is null
            ? null
            : AcpFlavor.Resolve(
                recorded.UseAgency,
                recorded.Model,
                recorded.ReasoningEffort,
                recorded.DisabledMcpServers);

    private LastEventInfo? TryReadLastEvent(string sessionId)
    {
        try
        {
            var path = Path.Combine(_scanner.Root, sessionId, "events.jsonl");
            if (!File.Exists(path)) return null;
            // Tail the last line. Cheap for a few-MB file; we read the
            // whole thing only if it's small. For large files we seek
            // from the end.
            var fi = new FileInfo(path);
            string? lastLine = null;
            if (fi.Length < 64 * 1024)
            {
                lastLine = File.ReadLines(path).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            }
            else
            {
                using var fs = File.OpenRead(path);
                fs.Seek(-Math.Min(64 * 1024, fs.Length), SeekOrigin.End);
                using var sr = new StreamReader(fs);
                _ = sr.ReadLine(); // discard partial first line
                string? l;
                while ((l = sr.ReadLine()) != null)
                    if (!string.IsNullOrWhiteSpace(l)) lastLine = l;
            }
            if (lastLine is null) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(lastLine);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
            var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
            DateTimeOffset? ts = null;
            if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed))
                ts = parsed;
            return new LastEventInfo(type, id, ts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryReadLastEvent failed for {Sid}", sessionId);
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }
}

public sealed class SessionNotLoadableException(string sessionId, string message)
    : InvalidOperationException(message)
{
    public string SessionId { get; } = sessionId;
}
