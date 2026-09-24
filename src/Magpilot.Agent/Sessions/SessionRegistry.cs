using System.Collections.Concurrent;
using Magpilot.Agent.Runtime;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Bridges on-disk Copilot CLI sessions with the active agent runtime.
/// Maintains the set of "owned" sessionIds (those currently loaded into
/// that runtime) and orchestrates adoption of locked sessions.
/// </summary>
public sealed class SessionRegistry
{
    private readonly IAgentSessionRuntime _runtime;
    private readonly SessionScanner _scanner;
    private readonly HostOwnership _hostOwnership;
    private readonly YoloRegistry _yolo;
    private readonly ILogger<SessionRegistry> _logger;
    private readonly ConcurrentDictionary<string, byte> _owned = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new();
    // Current-process copy of the profile captured at host acquisition. The
    // persisted HostOwnership entry covers normal live launchers across agent
    // restarts; this copy also covers synthetic/dead host PIDs whose ownership
    // entry is pruned before a failed handback can be retried.
    private readonly ConcurrentDictionary<string, HostSessionProfile> _handoffProfile = new();

    public SessionRegistry(
        IAgentSessionRuntime runtime,
        SessionScanner scanner,
        HostOwnership hostOwnership,
        YoloRegistry yolo,
        ILogger<SessionRegistry> logger)
    {
        _runtime = runtime;
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
        _runtime.IsAttached(sessionId) &&
        !_runtime.IsQuarantined(sessionId);

    public IReadOnlyList<SessionInfo> List() =>
        _scanner.Enumerate().Select(Project).ToList();

    public SessionInfo? Get(string id)
    {
        var metadata = _scanner.Get(id);
        return metadata is null ? null : Project(metadata);
    }

    public async Task<IReadOnlyList<SessionModelOption>> ListModelOptionsAsync(
        string sessionId,
        CancellationToken ct)
    {
        var state = GetState(sessionId)
            ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
        if (state.Owner != SessionOwner.Agent)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} must be agent-owned before its model options can be read.");
        }
        if (state.RuntimeStatus?.CanEditModel != true)
        {
            throw new SessionModelUpdateException(
                $"Session {sessionId} does not support live model changes on its current runtime.");
        }

        return await _runtime.ListModelOptionsAsync(sessionId, ct);
    }

    public async Task<SessionStateInfo> UpdateModelAsync(
        string sessionId,
        SessionModelUpdateRequest request,
        CancellationToken ct)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            var state = GetState(sessionId)
                ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
            if (state.Owner != SessionOwner.Agent)
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} must be agent-owned before changing its model.");
            }
            if (state.Activity == SessionActivity.InFlight)
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} cannot change model while a turn is in flight.");
            }
            if (state.RuntimeStatus?.CanEditModel != true)
            {
                throw new SessionModelUpdateException(
                    $"Session {sessionId} does not support live model changes on its current runtime.");
            }

            var options = await _runtime.ListModelOptionsAsync(sessionId, ct);
            var selected = options.FirstOrDefault(option =>
                string.Equals(option.Id, request.Model, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                throw new SessionModelUpdateException(
                    $"Model '{request.Model}' is not available for session {sessionId}.");
            }

            var current = _runtime.EffectiveProfile(sessionId)
                ?? throw new InvalidOperationException(
                    $"Session {sessionId} has no effective runtime profile.");
            var reasoning = SessionModelSelection.ResolveReasoningEffort(
                request.ReasoningEffort,
                current.ReasoningEffort,
                selected);
            await _runtime.ApplyOwnedConfigurationAsync(
                sessionId,
                current with
                {
                    Model = selected.Id,
                    ReasoningEffort = reasoning,
                },
                processScopeSpecified: false,
                ct);

            return GetState(sessionId)
                ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
        }
        finally
        {
            gate.Release();
        }
    }

    // cwd for a session, from its on-disk workspace.yaml; backs the recycle
    // reload path so the runtime stays free of the scanner.
    internal string? CwdFor(string sessionId) => _scanner.Get(sessionId)?.Cwd;

    private SessionInfo Project(SessionMetadata metadata) =>
        Project(metadata, ObserveOwnership(metadata.Id));

    private SessionInfo Project(
        SessionMetadata metadata,
        SessionOwnershipSnapshot ownership)
    {
        var state = ownership.Owner switch
        {
            SessionOwner.Agent => SessionState.Owned,
            SessionOwner.None => SessionState.Dormant,
            _ => SessionState.Locked,
        };
        var ownerPid = ownership.ForeignHolderPids.FirstOrDefault();
        if (ownerPid == 0)
            ownerPid = ownership.HostHolderPids.FirstOrDefault();
        if (ownerPid == 0)
            ownerPid = ownership.Host?.HostPid ?? 0;

        return new SessionInfo(
            metadata.Id,
            state,
            metadata.Cwd,
            metadata.Repository,
            metadata.Branch,
            metadata.Summary,
            ownerPid == 0 ? null : ownerPid,
            metadata.CreatedAt,
            metadata.UpdatedAt,
            _yolo.IsEnabled(metadata.Id));
    }

    private SessionOwnershipSnapshot ObserveOwnership(string sessionId)
    {
        var directory = Path.Combine(_scanner.Root, sessionId);
        var locks = SessionLocks.ReadSnapshot(directory);
        var livePids = locks.Live.Select(holder => holder.Pid).Distinct().ToArray();
        var runtimeOwned = IsAgentOwned(sessionId);
        var runtimeForeign = _runtime.ForeignLiveHolderPids(sessionId).ToHashSet();
        HostOwnerEntry? host = _hostOwnership.TryGet(sessionId, out var hostEntry)
            ? hostEntry
            : null;
        var hostHolderPids = host is { } currentHost
            ? livePids.Where(pid =>
                    pid == currentHost.HostPid ||
                    ProcessAncestry.IsSelfOrDescendantOf(pid, currentHost.HostPid))
                .ToArray()
            : [];
        var hostHolderSet = hostHolderPids.ToHashSet();
        var foreignHolderPids = livePids.Where(pid =>
                runtimeOwned
                    ? runtimeForeign.Contains(pid)
                    : host is not null
                        ? !hostHolderSet.Contains(pid)
                        : true)
            .ToArray();
        var contended =
            (runtimeOwned && (host is not null || foreignHolderPids.Length > 0)) ||
            (host is not null && foreignHolderPids.Length > 0);
        var owner = contended
            ? SessionOwner.Contended
            : host is not null
                ? SessionOwner.Host
                : runtimeOwned
                    ? SessionOwner.Agent
                    : foreignHolderPids.Length > 0
                        ? SessionOwner.External
                        : SessionOwner.None;

        return new SessionOwnershipSnapshot(
            owner,
            host,
            hostHolderPids,
            foreignHolderPids);
    }

    private sealed record SessionOwnershipSnapshot(
        SessionOwner Owner,
        HostOwnerEntry? Host,
        IReadOnlyList<int> HostHolderPids,
        IReadOnlyList<int> ForeignHolderPids);

    private async Task<SemaphoreSlim> AcquireLifecycleGateAsync(string sessionId, CancellationToken ct)
    {
        var gate = _lifecycleGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return gate;
    }

    public async Task<SessionInfo> CreateAsync(
        string? cwd,
        CancellationToken ct,
        string? name = null,
        string? model = null,
        string? reasoningEffort = null,
        string[]? disableMcpServers = null,
        string? agent = null,
        string[]? availableTools = null,
        bool noCustomInstructions = false,
        string? copilotHome = null)
    {
        SessionRuntimeProfile.ValidateAvailableToolsRequest(availableTools);
        cwd ??= Environment.CurrentDirectory;
        var profile = SessionRuntimeProfile.Resolve(
            model,
            reasoningEffort,
            disableMcpServers,
            agent,
            availableTools,
            noCustomInstructions,
            copilotHome);
        // The runtime invokes onAttached only after the complete requested
        // configuration has been applied and verified. A failed configure may
        // leave a quarantined route for retry, but it is not advertised as Owned.
        var sid = await _runtime.NewSessionAsync(cwd, profile, ct, onAttached: id => _owned.TryAdd(id, 0));

        // Stamp a friendly name into workspace.yaml if the caller supplied
        // one. Copilot CLI auto-derives a summary from the first user
        // message, which is awful for sessions whose first message is a
        // long prompt template (e.g. cron heartbeats). A user-set name
        // overrides that.
        if (!string.IsNullOrWhiteSpace(name))
            TryWriteWorkspaceField(sid, "name", name, "summary", name);

        return Get(sid)
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
    /// then reload it into the selected runtime.
    /// Callers may re-supply agent/model/reasoning/process-scope settings to
    /// restore the same runtime behavior after the load. Process-scoped profile
    /// settings are not persisted in Copilot's session files.
    /// </summary>
    public async Task<SessionInfo> AdoptAsync(
        string sessionId,
        bool force,
        CancellationToken ct,
        string? model = null,
        string? reasoningEffort = null,
        string[]? disableMcpServers = null,
        string? agent = null,
        string[]? availableTools = null,
        bool? noCustomInstructions = null,
        string? copilotHome = null)
    {
        SessionRuntimeProfile.ValidateAvailableToolsRequest(availableTools);
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            return await AdoptCoreAsync(
                sessionId,
                force,
                ct,
                model,
                reasoningEffort,
                disableMcpServers,
                agent,
                availableTools,
                noCustomInstructions,
                copilotHome);
        }
        finally { gate.Release(); }
    }

    private async Task<SessionInfo> AdoptCoreAsync(
        string sessionId,
        bool force,
        CancellationToken ct,
        string? model,
        string? reasoningEffort,
        string[]? disableMcpServers,
        string? agent,
        string[]? availableTools,
        bool? noCustomInstructions,
        string? copilotHome)
    {
        var info = Get(sessionId)
            ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
        if (_hostOwnership.TryGet(sessionId, out var host))
        {
            throw new InvalidOperationException(
                $"Session is held by magpilot launcher PID {host.HostPid}; release it from the host before adopting.");
        }
        var requestedProfile = SessionRuntimeProfile.Resolve(
            model,
            reasoningEffort,
            disableMcpServers,
            agent,
            availableTools,
            noCustomInstructions ?? false,
            copilotHome);
        var processScopeSpecified =
            disableMcpServers is not null ||
            agent is not null ||
            availableTools is not null ||
            noCustomInstructions is not null ||
            copilotHome is not null;

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
        if (_runtime.IsAttached(sessionId) &&
            _runtime.IsQuarantined(sessionId) &&
            !_hostOwnership.TryGet(sessionId, out _))
        {
            await _runtime.ApplyOwnedConfigurationAsync(
                sessionId,
                requestedProfile,
                processScopeSpecified,
                ct);
            _owned.TryAdd(sessionId, 0);
            return CompleteAdopt(sessionId, Get(sessionId) ?? info);
        }

        // Recycling a shared child invalidates every route it hosted. The stale
        // registry marker is intentionally enough to recognize one of our
        // invalidated bystanders even if its old live-looking lock has not yet
        // disappeared; reload it from disk instead of treating that lock as an
        // unrelated process that requires force.
        if (_owned.ContainsKey(sessionId) && !_runtime.IsAttached(sessionId))
        {
            var retainedProfile = _runtime.EffectiveProfile(sessionId);
            var reloadProfile = retainedProfile is null
                ? requestedProfile
                : SessionRuntimeProfile.Resolve(
                    model ?? retainedProfile.Model,
                    reasoningEffort ?? retainedProfile.ReasoningEffort,
                    disableMcpServers ?? retainedProfile.DisabledMcpServers,
                    agent ?? retainedProfile.Agent,
                    availableTools ?? retainedProfile.AvailableTools,
                    noCustomInstructions ?? retainedProfile.NoCustomInstructions,
                    copilotHome ?? retainedProfile.CopilotHome);
            _logger.LogWarning(
                "Session {Sid} was invalidated with its co-hosted runtime; re-attaching it from disk",
                sessionId);
            await _runtime.ReloadFromDiskAsync(
                sessionId,
                info.Cwd ?? Environment.CurrentDirectory,
                reloadProfile,
                ct,
                onAttached: id => _owned.TryAdd(id, 0));
            return CompleteAdopt(
                sessionId,
                Get(sessionId) ?? info with { State = SessionState.Owned });
        }

        // A cancelled/timed-out session/load, or a normal detach, can leave the
        // session resident in one of our children without a published route.
        // Its live lock is ours, not a foreign owner that should require force.
        // Recycle the holder and retry from disk before the generic Locked path.
        if (!_runtime.IsAttached(sessionId) && _runtime.IsResident(sessionId))
        {
            var retainedProfile = _runtime.EffectiveProfile(sessionId);
            var reloadProfile = retainedProfile is null
                ? requestedProfile
                : SessionRuntimeProfile.Resolve(
                    model ?? retainedProfile.Model,
                    reasoningEffort ?? retainedProfile.ReasoningEffort,
                    disableMcpServers ?? retainedProfile.DisabledMcpServers,
                    agent ?? retainedProfile.Agent,
                    availableTools ?? retainedProfile.AvailableTools,
                    noCustomInstructions ?? retainedProfile.NoCustomInstructions,
                    copilotHome ?? retainedProfile.CopilotHome);
            await _runtime.ReloadFromDiskAsync(
                sessionId,
                info.Cwd ?? Environment.CurrentDirectory,
                reloadProfile,
                ct,
                onAttached: id => _owned.TryAdd(id, 0));
            return CompleteAdopt(
                sessionId,
                Get(sessionId) ?? info with { State = SessionState.Owned });
        }

        var eventsPath = Path.Combine(_scanner.Root, sessionId, "events.jsonl");
        if (!_runtime.IsAttached(sessionId) && !File.Exists(eventsPath))
        {
            throw new SessionNotLoadableException(
                sessionId,
                $"Session {sessionId} has no events.jsonl and is not resident in a live runtime; it cannot be loaded.");
        }

        if (info.State == SessionState.Owned)
        {
            // An attached session must still honor an adopt request's config.
            // Process-scoped MCP/tool/home changes cannot be made in place and
            // fail explicitly.
            await _runtime.ApplyOwnedConfigurationAsync(
                sessionId,
                requestedProfile,
                processScopeSpecified,
                ct);
            return CompleteAdopt(sessionId, Get(sessionId) ?? info);
        }

        if (info.State == SessionState.Locked)
        {
            if (!force) throw new InvalidOperationException("Session is held by another process; pass force=true to take over.");
            var evicted = _runtime.EvictForeignLiveHolders(sessionId);
            if (evicted.Count > 0)
            {
                _logger.LogWarning(
                    "Force-adopt evicted {Count} foreign holder(s) on {Sid}: {Pids}",
                    evicted.Count,
                    sessionId,
                    string.Join(", ", evicted));
            }
            if (_runtime.HasForeignLiveHolder(sessionId))
            {
                throw new InvalidOperationException(
                    $"Could not evict every foreign holder of session {sessionId}; it was not adopted.");
            }
        }

        var cwd = info.Cwd ?? Environment.CurrentDirectory;
        // Load using the requested process scope, then apply model/reasoning.
        // A bootstrap re-supplies process-scoped MCP exclusions on every boot
        // because that host profile is not persisted by Copilot.
        // Ownership is claimed only after configuration verifies. A failure keeps
        // a quarantined attached route that the early retry path above can
        // configure without issuing a second session/load.
        await _runtime.ReloadFromDiskAsync(sessionId, cwd, requestedProfile, ct, onAttached: id => _owned.TryAdd(id, 0));
        return CompleteAdopt(
            sessionId,
            Get(sessionId) ?? info with { State = SessionState.Owned });
    }

    private SessionInfo CompleteAdopt(string sessionId, SessionInfo info)
    {
        // A normal adopt after the old host exited supersedes that handoff.
        // Consume its durable record so a delayed /release cannot later repin
        // the old tuple or remove this newly restored ownership.
        _hostOwnership.Clear(sessionId);
        _handoffProfile.TryRemove(sessionId, out _);
        return info;
    }

    public async Task<SessionRuntimeProfile?> DetachAsync(
        string sessionId,
        CancellationToken ct,
        bool force = false)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            return await DetachCoreAsync(sessionId, ct, force);
        }
        finally { gate.Release(); }
    }

    public bool IsResident(string sessionId) => _runtime.IsResident(sessionId);

    private async Task<SessionRuntimeProfile?> DetachCoreAsync(
        string sessionId,
        CancellationToken ct,
        bool force)
    {
        if (!_runtime.IsAttached(sessionId))
        {
            var retainedProfile = force && _runtime.IsResident(sessionId)
                ? await _runtime.ForceDetachAsync(sessionId, ct)
                : _runtime.EffectiveProfile(sessionId);
            _owned.TryRemove(sessionId, out _);
            return retainedProfile;
        }

        var profile = force
            ? await _runtime.ForceDetachAsync(sessionId, ct)
            : await _runtime.CloseAsync(sessionId, ct);
        _owned.TryRemove(sessionId, out _);
        return profile;
    }

    /// <summary>
    /// Compute the rich ownership + activity view of a session for the
    /// magpilot launcher or SPA. Cheap: filesystem stat + a few
    /// in-memory lookups + an events.jsonl tail read.
    /// </summary>
    public SessionStateInfo? GetState(string sessionId)
    {
        var metadata = _scanner.Get(sessionId);
        if (metadata is null) return null;
        var ownership = ObserveOwnership(sessionId);
        var info = Project(metadata, ownership);

        SessionActivity activity;
        InFlightInfo? inFlight = null;
        if (_runtime.IsTurnInFlight(sessionId, out var entry))
        {
            activity = SessionActivity.InFlight;
            inFlight = new InFlightInfo(
                Driver: entry.Requester,
                StartedAtMs: entry.StartedAt.ToUnixTimeMilliseconds());
        }
        else
        {
            activity = SessionActivity.Idle;
        }

        var lastEvent = TryReadLastEvent(sessionId);

        return new SessionStateInfo(
            info,
            ownership.Owner,
            ownership.Host?.HostPid,
            ownership.Host?.LeaseId,
            activity,
            inFlight,
            lastEvent,
            _runtime.RuntimeStatus(sessionId),
            ownership.ForeignHolderPids);
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
            await _runtime.BeginSessionDrainAsync(sessionId, ct);
            draining = true;
            return await AcquireForHostCoreAsync(sessionId, hostPid, force, ct);
        }
        finally
        {
            if (draining)
                await _runtime.EndSessionDrainAsync(sessionId);
            gate.Release();
        }
    }

    private async Task<SessionStateInfo> AcquireForHostCoreAsync(string sessionId, int hostPid, bool force, CancellationToken ct)
    {
        var scanned = Get(sessionId);
        if (scanned is null)
            throw new FileNotFoundException($"Session {sessionId} not on disk");
        if (_hostOwnership.TryGet(sessionId, out var liveHost))
        {
            if (liveHost.HostPid == hostPid)
                return GetState(sessionId)!;
            throw new InvalidOperationException(
                $"Session is already held by host PID {liveHost.HostPid}; it must release before host PID {hostPid} can acquire it.");
        }
        var ownership = ObserveOwnership(sessionId);
        var unrelatedHolders = ownership.ForeignHolderPids
            .Where(pid =>
                pid != hostPid &&
                !ProcessAncestry.IsSelfOrDescendantOf(pid, hostPid))
            .ToArray();
        if (unrelatedHolders.Length > 0 && !force)
        {
            throw new InvalidOperationException(
                $"Session is held by external PID(s) {string.Join(", ", unrelatedHolders)}; " +
                "pass force=true to evict them before acquiring.");
        }

        if (unrelatedHolders.Length > 0)
        {
            var evicted = _runtime.EvictForeignLiveHolders(sessionId);
            if (_runtime.HasForeignLiveHolder(sessionId))
            {
                throw new InvalidOperationException(
                    $"Could not evict every external holder of session {sessionId}; host acquisition was not recorded.");
            }
            _logger.LogWarning(
                "AcquireForHost force-evicted {Count} external holder(s) on {Sid}: {Pids}",
                evicted.Count,
                sessionId,
                string.Join(", ", evicted));
        }

        // If we currently own the session, gracefully release it.
        HostSessionProfile? handbackProfile = null;
        if (_runtime.IsAttached(sessionId))
        {
            _handoffProfile.TryGetValue(sessionId, out var pendingProfile);
            var preservePendingProfile =
                _runtime.IsQuarantined(sessionId) &&
                pendingProfile is not null;
            // Capture what the session is actually running under BEFORE detaching:
            // process-scoped tool surface plus the applied model/reasoning.
            // Handback restores exactly this, so a launcher round-trip
            // never silently demotes a pinned session to the default profile.
            var forceDetach = false;
            if (_runtime.IsTurnInFlight(sessionId, out _))
            {
                if (force)
                {
                    _logger.LogInformation("AcquireForHost (force): cancelling in-flight turn on {Sid}", sessionId);
                    try { await _runtime.CancelAsync(sessionId, ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Runtime abort failed during force-acquire for {Sid}", sessionId); }
                    // Give the turn ~2s to finalize (it should emit TurnComplete).
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    grace.CancelAfter(TimeSpan.FromSeconds(2));
                    try { await _runtime.WaitForTurnBoundaryAsync(sessionId, grace.Token); }
                    catch (OperationCanceledException) { /* stop waiting; we'll detach anyway */ }
                    forceDetach = _runtime.IsTurnInFlight(sessionId, out _);
                }
                else
                {
                    _logger.LogInformation("AcquireForHost (polite): waiting for in-flight turn on {Sid}", sessionId);
                    await _runtime.WaitForTurnBoundaryAsync(sessionId, ct);
                }
            }
            var detachedProfile = Describe(await DetachCoreAsync(sessionId, ct, forceDetach));
            handbackProfile = preservePendingProfile ? pendingProfile : detachedProfile;
        }
        else
        {
            if (_handoffProfile.TryGetValue(sessionId, out var pendingProfile))
                handbackProfile = pendingProfile;
            else if (_hostOwnership.TryGetRecorded(sessionId, out var existingHost))
                handbackProfile = existingHost.Profile;
            else
                handbackProfile = Describe(_runtime.EffectiveProfile(sessionId));
            _owned.TryRemove(sessionId, out _);
        }

        if (handbackProfile is not null)
            _handoffProfile[sessionId] = handbackProfile;
        else
            _handoffProfile.TryRemove(sessionId, out _);
        var hostLease = _hostOwnership.Set(sessionId, hostPid, handbackProfile);

        // Drop the in-memory yolo bit so the terminal user (now at a
        // real keyboard with a TTY) gets the standard interactive
        // approval prompts rather than silent auto-approve they didn't
        // opt into. The wrapper can flip yolo back on later via the
        // agent's HTTP API if desired.
        _yolo.Clear(sessionId);

        // Refresh state for the response (now reflecting host ownership).
        var state = GetState(sessionId)!;
        return state with
        {
            HostPid = hostPid,
            HostLeaseId = hostLease.LeaseId,
        };
    }

    /// <summary>
    /// Hand a host-owned session back to the agent. In the cooperative case the
    /// launcher has already shut its child Copilot down. The lease must match the
    /// exact terminal acquisition. The agent retains the lease and declines to
    /// attach while any live foreign holder remains.
    /// Adopting while a foreign copilot still writes would split-brain one session
    /// across two drivers (the "garbled then stalled" failure), so we never adopt
    /// while a live foreign holder remains.
    /// </summary>
    public async Task<SessionStateInfo> ReleaseFromHostAsync(string sessionId, Guid leaseId, CancellationToken ct)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            return await ReleaseFromHostCoreAsync(sessionId, leaseId, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<SessionStateInfo> ReleaseFromHostCoreAsync(string sessionId, Guid leaseId, CancellationToken ct)
    {
        var info = Get(sessionId)
            ?? throw new FileNotFoundException($"Session {sessionId} not on disk");

        HostSessionProfile? recordedProfile = null;
        var hasReleaseRecord = false;
        if (_hostOwnership.TryGetRecorded(sessionId, out var entry))
        {
            if (entry.LeaseId != leaseId)
                throw new InvalidOperationException(
                    $"Session is held by lease {entry.LeaseId}, not {leaseId}; cannot release on its behalf.");
            recordedProfile = entry.Profile;
            hasReleaseRecord = true;
        }
        if (_handoffProfile.TryGetValue(sessionId, out var pendingProfile))
        {
            recordedProfile = pendingProfile;
            hasReleaseRecord = true;
        }
        if (!hasReleaseRecord)
        {
            _logger.LogInformation(
                "Ignoring stale ReleaseFromHost for {Sid} with lease {LeaseId}; no matching handoff remains",
                sessionId,
                leaseId);
            return GetState(sessionId)!;
        }

        // Never adopt while a live foreign copilot still holds the session: two
        // live drivers on one events.jsonl is the split-brain. The launcher must
        // stop its child before releasing. Forceful eviction is a separate
        // agent-side take-over transition.
        if (_runtime.HasForeignLiveHolder(sessionId))
        {
            throw new InvalidOperationException(
                $"A live foreign holder remains on session {sessionId}; terminal release was not accepted.");
        }

        // Re-load the session into the recorded runtime profile. The terminal
        // appended to events.jsonl while it was driving, so the runtime must
        // reconnect from disk rather than serve its pre-handoff snapshot. The
        // session comes back with the same process/tool scope, model, and
        // reasoning. The runtime may retain a quarantined route if
        // configuration fails, but registry ownership is not restored until the
        // complete tuple verifies.
        var cwd = info.Cwd ?? Environment.CurrentDirectory;
        var profile = ResolveProfile(recordedProfile)
            ?? _runtime.EffectiveProfile(sessionId)
            ?? SessionRuntimeProfile.Default;
        try
        {
            if (_runtime.IsAttached(sessionId))
            {
                await _runtime.ApplyOwnedConfigurationAsync(
                    sessionId,
                    profile,
                    processScopeSpecified: true,
                    ct);
            }
            else
            {
                await _runtime.ReloadFromDiskAsync(sessionId, cwd, profile, ct);
            }

            _owned.TryAdd(sessionId, 0);
            _hostOwnership.Clear(sessionId);
            _handoffProfile.TryRemove(sessionId, out _);
        }
        catch (Exception ex)
        {
            _owned.TryRemove(sessionId, out _);
            // Keep host ownership and its recorded profile. Either attach never
            // happened, or the runtime retained a quarantined route that the next
            // release attempt can configure in place. In neither case do we
            // advertise the session as agent-owned.
            _logger.LogWarning(
                ex,
                "Re-attaching {Sid} during ReleaseFromHost failed",
                sessionId);
            throw new SessionRuntimeConfigurationException(
                $"Re-attaching session {sessionId} during terminal handback failed.",
                ex)
            {
                SessionId = sessionId,
                LeavesSessionIndeterminate = _runtime.IsAttached(sessionId),
            };
        }

        return GetState(sessionId)!;
    }

    public async Task<SessionStateInfo> TakeOverForAgentAsync(
        string sessionId,
        bool force,
        CancellationToken ct)
    {
        var gate = await AcquireLifecycleGateAsync(sessionId, ct);
        try
        {
            var state = GetState(sessionId)
                ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
            if (state.Owner == SessionOwner.Agent)
                return state;
            if (!force && state.Owner is SessionOwner.Host or SessionOwner.External or SessionOwner.Contended)
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} is {state.Owner}; force is required to take it over.");
            }

            if (force)
            {
                var evicted = _runtime.EvictForeignLiveHolders(sessionId);
                if (evicted.Count > 0)
                {
                    _logger.LogWarning(
                        "Agent take-over evicted {Count} foreign holder(s) on {Sid}: {Pids}",
                        evicted.Count,
                        sessionId,
                        string.Join(", ", evicted));
                }
                if (_runtime.HasForeignLiveHolder(sessionId))
                {
                    throw new InvalidOperationException(
                        $"A foreign holder remains on session {sessionId}; it was not attached.");
                }
            }

            HostSessionProfile? recordedProfile = null;
            if (_handoffProfile.TryGetValue(sessionId, out var pendingProfile))
                recordedProfile = pendingProfile;
            else if (_hostOwnership.TryGetRecorded(sessionId, out var recorded))
                recordedProfile = recorded.Profile;
            var profile = ResolveProfile(recordedProfile)
                ?? _runtime.EffectiveProfile(sessionId)
                ?? SessionRuntimeProfile.Default;

            if (_runtime.IsAttached(sessionId))
            {
                if (_runtime.IsQuarantined(sessionId))
                {
                    await _runtime.ApplyOwnedConfigurationAsync(
                        sessionId,
                        profile,
                        processScopeSpecified: true,
                        ct);
                }
            }
            else
            {
                var metadata = _scanner.Get(sessionId)
                    ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
                await _runtime.ReloadFromDiskAsync(
                    sessionId,
                    metadata.Cwd ?? Environment.CurrentDirectory,
                    profile,
                    ct);
            }

            _owned.TryAdd(sessionId, 0);
            _hostOwnership.Clear(sessionId);
            _handoffProfile.TryRemove(sessionId, out _);
            return GetState(sessionId)
                ?? throw new FileNotFoundException($"Session {sessionId} not on disk");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Snapshot a runtime profile as the plain record the ownership map
    /// persists, so a handback can rebuild it after an agent restart.
    /// </summary>
    private static HostSessionProfile? Describe(SessionRuntimeProfile? profile) =>
        profile is null
            ? null
            : new HostSessionProfile(
                Model: profile.Model,
                ReasoningEffort: profile.ReasoningEffort,
                DisabledMcpServers: profile.DisabledMcpServers?.ToArray(),
                Agent: profile.Agent,
                AvailableTools: profile.AvailableTools?.ToArray(),
                NoCustomInstructions: profile.NoCustomInstructions,
                CopilotHome: profile.CopilotHome);

    private static SessionRuntimeProfile? ResolveProfile(HostSessionProfile? recorded) =>
        recorded is null
            ? null
            : SessionRuntimeProfile.Resolve(
                recorded.Model,
                recorded.ReasoningEffort,
                recorded.DisabledMcpServers,
                recorded.Agent,
                recorded.AvailableTools,
                recorded.NoCustomInstructions,
                recorded.CopilotHome);

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

}

public sealed class SessionNotLoadableException(string sessionId, string message)
    : InvalidOperationException(message)
{
    public string SessionId { get; } = sessionId;
}
