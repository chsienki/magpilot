#pragma warning disable GHCP001

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using GitHub.Copilot;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Runtime.Sdk;

internal sealed class SdkSessionRuntime(
    SdkClientPool clients,
    SdkPermissionBroker permissions,
    ILogger<SdkSessionRuntime> log)
    : IAgentSessionRuntime
{
    private readonly ConcurrentDictionary<string, AttachedSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ActiveTurn> _activeTurns = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>>
        _ourSessionPids = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<string, byte> _draining = new();
    private readonly Dictionary<string, List<Channel<StreamEvent>>> _subscribers = new();
    private readonly object _subscriberLock = new();
    private readonly SemaphoreSlim _routingGate = new(1, 1);
    private readonly string _sessionStateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot",
        "session-state");

    public bool IsQuarantined(string sessionId) => false;

    public bool IsAttached(string sessionId) => _sessions.ContainsKey(sessionId);

    public bool IsResident(string sessionId) => IsAttached(sessionId);

    public SessionRuntimeProfile? EffectiveProfile(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var attached)
            ? attached.Profile
            : null;

    public bool IsTurnInFlight(
        string sessionId,
        out SessionInFlightEntry entry)
    {
        if (_activeTurns.TryGetValue(sessionId, out var turn))
        {
            entry = new SessionInFlightEntry(turn.Requester, turn.StartedAt);
            return true;
        }

        entry = default;
        return false;
    }

    public async Task WaitForTurnBoundaryAsync(
        string sessionId,
        CancellationToken ct)
    {
        if (_activeTurns.TryGetValue(sessionId, out var turn))
            await turn.Completion.Task.WaitAsync(ct);
    }

    public async Task BeginSessionDrainAsync(
        string sessionId,
        CancellationToken ct)
    {
        await _routingGate.WaitAsync(ct);
        try
        {
            _draining[sessionId] = 0;
        }
        finally
        {
            _routingGate.Release();
        }
    }

    public async Task EndSessionDrainAsync(string sessionId)
    {
        await _routingGate.WaitAsync(CancellationToken.None);
        try
        {
            _draining.TryRemove(sessionId, out _);
        }
        finally
        {
            _routingGate.Release();
        }
    }

    public async Task<string> NewSessionAsync(
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached = null)
    {
        ValidateProfile(profile);
        var sessionId = Guid.NewGuid().ToString();
        var host = await clients.AcquireAsync(profile, ct);
        var lifetime = new CancellationTokenSource();
        var config = SdkSessionProfileMapper.Create(profile, cwd, sessionId);
        config.OnPermissionRequest =
            permissions.CreateHandler(sessionId, Publish, lifetime.Token);

        CopilotSession session;
        try
        {
            session = await host.Client.CreateSessionAsync(config, ct);
        }
        catch (Exception ex)
        {
            lifetime.Dispose();
            throw WrapConfigurationFailure(
                $"SDK session creation failed for {sessionId}.",
                sessionId,
                ex);
        }

        var attached = Attach(session, host, profile, lifetime);
        if (!_sessions.TryAdd(sessionId, attached))
        {
            await DisposeAttachedAsync(attached);
            throw new InvalidOperationException(
                $"Session {sessionId} was attached twice.");
        }

        await MarkOurRuntimeHoldersAsync(sessionId, ct);
        onAttached?.Invoke(sessionId);
        log.LogInformation(
            "New SDK session {SessionId} cwd={Cwd} baseDirectory={BaseDirectory}",
            sessionId,
            cwd,
            profile.CopilotHome ?? "(default)");
        return sessionId;
    }

    public async Task ReloadFromDiskAsync(
        string sessionId,
        string cwd,
        SessionRuntimeProfile profile,
        CancellationToken ct,
        Action<string>? onAttached = null)
    {
        ValidateProfile(profile);
        if (_sessions.ContainsKey(sessionId))
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is already attached to an SDK runtime.");
        }

        var host = await clients.AcquireAsync(profile, ct);
        var lifetime = new CancellationTokenSource();
        var config = SdkSessionProfileMapper.Resume(profile, cwd);
        config.OnPermissionRequest =
            permissions.CreateHandler(sessionId, Publish, lifetime.Token);

        CopilotSession session;
        try
        {
            session = await host.Client.ResumeSessionAsync(
                sessionId,
                config,
                ct);
        }
        catch (Exception ex)
        {
            lifetime.Dispose();
            throw WrapConfigurationFailure(
                $"SDK session resume failed for {sessionId}.",
                sessionId,
                ex);
        }

        var attached = Attach(session, host, profile, lifetime);
        if (!_sessions.TryAdd(sessionId, attached))
        {
            await DisposeAttachedAsync(attached);
            throw new InvalidOperationException(
                $"Session {sessionId} was attached twice.");
        }

        await MarkOurRuntimeHoldersAsync(sessionId, ct);
        onAttached?.Invoke(sessionId);
        log.LogInformation(
            "Resumed SDK session {SessionId} cwd={Cwd} baseDirectory={BaseDirectory}",
            sessionId,
            cwd,
            profile.CopilotHome ?? "(default)");
    }

    public async Task ApplyOwnedConfigurationAsync(
        string sessionId,
        SessionRuntimeProfile requested,
        bool processScopeSpecified,
        CancellationToken ct)
    {
        ValidateProfile(requested);
        var gate = await AcquireSessionGateAsync(sessionId, ct);
        try
        {
            var attached = SessionFor(sessionId);
            var current = attached.Profile;
            if (requested.Backend != current.Backend)
            {
                throw new SessionRuntimeConfigurationException(
                    $"Session {sessionId} is attached to {current.Backend}, not {requested.Backend}.");
            }

            if (processScopeSpecified && !SameProcessScope(current, requested))
            {
                throw new SessionRuntimeConfigurationException(
                    $"Session {sessionId} is already attached with a different SDK process/tool scope.");
            }

            if (requested.Agent is not null &&
                !string.Equals(
                    requested.Agent,
                    current.Agent,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionRuntimeConfigurationException(
                    "Changing the selected custom agent on an attached SDK session is not implemented.");
            }

            var model = requested.Model ?? current.Model;
            var reasoning = requested.ReasoningEffort
                ?? current.ReasoningEffort;
            if (!string.Equals(model, current.Model, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    reasoning,
                    current.ReasoningEffort,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (model is null)
                {
                    throw new SessionRuntimeConfigurationException(
                        "Changing SDK reasoning effort requires a known model.");
                }

                await attached.Session.SetModelAsync(
                    model,
                    new SetModelOptions { ReasoningEffort = reasoning },
                    ct);
            }

            attached.Profile = current with
            {
                Model = model,
                ReasoningEffort = reasoning,
            };
        }
        catch (SessionRuntimeConfigurationException ex)
        {
            ex.SessionId ??= sessionId;
            throw;
        }
        catch (Exception ex)
        {
            throw WrapConfigurationFailure(
                $"SDK configuration failed for session {sessionId}.",
                sessionId,
                ex);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool MayBeStale(string sessionId) => false;

    public void ResyncWatermark(string sessionId)
    {
    }

    public Task<SessionRecycleOutcome> RecycleForStaleAsync(
        string sessionId,
        Func<string, string?> cwdResolver,
        CancellationToken ct) =>
        Task.FromResult(SessionRecycleOutcome.NotLoaded);

    public bool HasForeignLiveHolder(string sessionId)
    {
        var ours = _ourSessionPids.TryGetValue(sessionId, out var known)
            ? known
            : null;
        return SessionLocks.Live(
                SessionLocks.Inspect(SessionDirectory(sessionId)))
            .Any(holder => ours is null || !ours.ContainsKey(holder.Pid));
    }

    public IReadOnlyList<int> EvictForeignLiveHolders(string sessionId)
    {
        var directory = SessionDirectory(sessionId);
        var ours = _ourSessionPids.TryGetValue(sessionId, out var known)
            ? known
            : null;
        var live = SessionLocks.Live(SessionLocks.Inspect(directory))
            .Where(holder => ours is null || !ours.ContainsKey(holder.Pid))
            .ToArray();
        foreach (var holder in live)
        {
            try
            {
                using var process = Process.GetProcessById(holder.Pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                log.LogWarning(
                    ex,
                    "Could not evict foreign holder PID {Pid} on SDK session {SessionId}",
                    holder.Pid,
                    sessionId);
            }
        }

        SessionLocks.ReapDead(directory);
        return live.Select(static holder => holder.Pid).ToArray();
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
        promptCt.ThrowIfCancellationRequested();
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

        ActiveTurn? turn = null;
        try
        {
            if (_draining.ContainsKey(sessionId))
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} is draining for host handoff and is not accepting new prompts.");
            }

            var attached = SessionFor(sessionId);
            turn = new ActiveTurn(
                requester,
                DateTimeOffset.UtcNow,
                gate);
            turn.Mapper.BeginTurn();
            if (!_activeTurns.TryAdd(sessionId, turn))
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} already has an SDK turn in flight.");
            }

            turn.CancellationRegistration = promptCt.Register(
                () => _ = AbortAfterCancellationAsync(sessionId));

            var promptText = string.IsNullOrEmpty(source)
                ? text
                : $"[via {source}] {text}";
            if (!string.IsNullOrEmpty(source))
                Publish(sessionId, new UserDelta(promptText, source));

            await attached.Session.SendAsync(
                new MessageOptions
                {
                    Prompt = promptText,
                    DisplayPrompt = text,
                    Source = string.IsNullOrEmpty(source)
                        ? MessageSource.User
                        : MessageSource.Agent(source),
                },
                CancellationToken.None);
            return turn.Completion.Task;
        }
        catch
        {
            if (turn is not null)
                FailTurn(sessionId, turn, "The SDK runtime did not accept the prompt.");
            else
                gate.Release();
            throw;
        }
        finally
        {
            _routingGate.Release();
        }
    }

    public async Task PromptAsync(
        string sessionId,
        string text,
        CancellationToken ct,
        string? requester = null,
        string? source = null)
    {
        try
        {
            var turn = await StartPromptAsync(
                sessionId,
                text,
                ct,
                ct,
                requester,
                source);
            await turn;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "SDK prompt failed for {SessionId}", sessionId);
        }
    }

    public void PublishToSubscribers(string sessionId, StreamEvent evt) =>
        Publish(sessionId, evt);

    public async Task CancelAsync(string sessionId, CancellationToken ct)
    {
        var attached = SessionFor(sessionId);
        await attached.Session.AbortAsync(ct);
    }

    public async Task<SessionRuntimeProfile?> CloseAsync(
        string sessionId,
        string? sessionsRoot,
        CancellationToken ct)
    {
        var gate = await AcquireSessionGateAsync(sessionId, ct);
        try
        {
            if (!_sessions.TryGetValue(sessionId, out var attached))
                return null;

            permissions.CancelSession(sessionId);
            await attached.Session.DisposeAsync();
            if (_sessions.TryRemove(
                    new KeyValuePair<string, AttachedSession>(
                        sessionId,
                        attached)))
            {
                await DisposeAttachedStateAsync(attached);
                _ourSessionPids.TryRemove(sessionId, out _);
            }
            return attached.Profile;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SessionRuntimeProfile?> ForceDetachAsync(
        string sessionId,
        CancellationToken ct)
    {
        if (_activeTurns.TryGetValue(sessionId, out var turn))
        {
            await CancelAsync(sessionId, ct);
            try
            {
                await turn.Completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(30),
                    ct);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    $"SDK session {sessionId} did not reach a boundary after abort.");
            }
        }

        return await CloseAsync(sessionId, sessionsRoot: null, ct);
    }

    public ChannelReader<StreamEvent> Subscribe(string sessionId)
    {
        var channel = Channel.CreateUnbounded<StreamEvent>(
            new UnboundedChannelOptions { SingleReader = true });
        lock (_subscriberLock)
        {
            if (!_subscribers.TryGetValue(sessionId, out var subscribers))
                _subscribers[sessionId] = subscribers = [];
            subscribers.Add(channel);
        }
        return channel.Reader;
    }

    public void Unsubscribe(
        string sessionId,
        ChannelReader<StreamEvent> reader)
    {
        lock (_subscriberLock)
        {
            if (!_subscribers.TryGetValue(sessionId, out var subscribers))
                return;
            subscribers.RemoveAll(
                channel => ReferenceEquals(channel.Reader, reader));
            if (subscribers.Count == 0)
                _subscribers.Remove(sessionId);
        }
    }

    public bool ResolveApproval(string approvalId, string optionId) =>
        permissions.Resolve(approvalId, optionId);

    public async Task<int> SweepStalledTurnsAsync(
        TimeSpan threshold,
        Func<string, string?> cwdResolver,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var recovered = 0;
        foreach (var (sessionId, turn) in _activeTurns.ToArray())
        {
            bool stalled;
            lock (turn.Sync)
            {
                stalled =
                    turn.OpenToolCalls.Count == 0 &&
                    now - turn.LastEventAt >= threshold;
            }
            if (!stalled)
                continue;

            try
            {
                await SessionFor(sessionId).Session.AbortAsync(ct);
                if (FailTurn(
                        sessionId,
                        turn,
                        "The assistant stalled and was aborted. Please try again."))
                {
                    recovered++;
                }
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "SDK turn watchdog could not abort stalled session {SessionId}",
                    sessionId);
            }
        }
        return recovered;
    }

    private AttachedSession Attach(
        CopilotSession session,
        ISdkClientHost host,
        SessionRuntimeProfile profile,
        CancellationTokenSource lifetime)
    {
        var attached = new AttachedSession(
            session,
            host,
            profile,
            lifetime);
        attached.Subscription = session.On<SessionEvent>(
            evt => HandleEvent(session.SessionId, evt));
        return attached;
    }

    private void HandleEvent(string sessionId, SessionEvent evt)
    {
        if (!_activeTurns.TryGetValue(sessionId, out var turn))
            return;

        IReadOnlyList<StreamEvent> mapped;
        lock (turn.Sync)
        {
            turn.LastEventAt = DateTimeOffset.UtcNow;
            mapped = turn.Mapper.Map(evt);
            foreach (var streamEvent in mapped)
            {
                switch (streamEvent)
                {
                    case ToolCallStart start:
                        turn.OpenToolCalls.Add(start.ToolCallId);
                        break;
                    case ToolCallEnd end:
                        turn.OpenToolCalls.Remove(end.ToolCallId);
                        break;
                }
            }
        }

        foreach (var streamEvent in mapped)
            Publish(sessionId, streamEvent);

        if (mapped.Any(static evt => evt is TurnComplete))
            CompleteTurn(sessionId, turn);
    }

    private bool FailTurn(
        string sessionId,
        ActiveTurn turn,
        string message)
    {
        IReadOnlyList<StreamEvent> events;
        lock (turn.Sync)
            events = turn.Mapper.Fail(message);

        foreach (var evt in events)
            Publish(sessionId, evt);
        return events.Any(static evt => evt is TurnComplete)
            && CompleteTurn(sessionId, turn);
    }

    private bool CompleteTurn(string sessionId, ActiveTurn turn)
    {
        if (!_activeTurns.TryRemove(
                new KeyValuePair<string, ActiveTurn>(
                    sessionId,
                    turn)))
        {
            return false;
        }

        turn.CancellationRegistration.Dispose();
        turn.Completion.TrySetResult();
        turn.Gate.Release();
        return true;
    }

    private async Task AbortAfterCancellationAsync(string sessionId)
    {
        try
        {
            if (_sessions.TryGetValue(sessionId, out var attached))
                await attached.Session.AbortAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(
                ex,
                "SDK abort after caller cancellation failed for {SessionId}",
                sessionId);
        }
    }

    private void Publish(string sessionId, StreamEvent evt)
    {
        List<Channel<StreamEvent>>? subscribers;
        lock (_subscriberLock)
        {
            _subscribers.TryGetValue(sessionId, out subscribers);
            subscribers = subscribers?.ToList();
        }
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers)
            subscriber.Writer.TryWrite(evt);
    }

    private AttachedSession SessionFor(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var attached)
            ? attached
            : throw new InvalidOperationException(
                $"Session {sessionId} is not attached to an SDK runtime.");

    private async Task<SemaphoreSlim> AcquireSessionGateAsync(
        string sessionId,
        CancellationToken ct)
    {
        var gate = _sessionGates.GetOrAdd(
            sessionId,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return gate;
    }

    private string SessionDirectory(string sessionId) =>
        Path.Combine(_sessionStateRoot, sessionId);

    private async Task MarkOurRuntimeHoldersAsync(
        string sessionId,
        CancellationToken ct)
    {
        var known = _ourSessionPids.GetOrAdd(
            sessionId,
            static _ => new ConcurrentDictionary<int, byte>());
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var live = SessionLocks.Live(
                SessionLocks.Inspect(SessionDirectory(sessionId)));
            foreach (var holder in live)
                known.TryAdd(holder.Pid, 0);
            if (live.Count > 0)
                return;
            await Task.Delay(100, ct);
        }
    }

    private static void ValidateProfile(SessionRuntimeProfile profile)
    {
        if (profile.Backend != SessionRuntimeBackend.Sdk)
        {
            throw new SessionRuntimeConfigurationException(
                $"SDK runtime received a {profile.Backend} profile.");
        }
        try
        {
            _ = SdkSessionProfileMapper.Create(
                profile,
                Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is NotSupportedException)
        {
            throw new SessionRuntimeConfigurationException(
                ex.Message,
                ex);
        }
    }

    private static bool SameProcessScope(
        SessionRuntimeProfile left,
        SessionRuntimeProfile right) =>
        string.Equals(
            left.CopilotHome,
            right.CopilotHome,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal) &&
        (left.DisabledMcpServers ?? []).SequenceEqual(
            right.DisabledMcpServers ?? [],
            StringComparer.OrdinalIgnoreCase) &&
        (left.AvailableTools ?? []).SequenceEqual(
            right.AvailableTools ?? [],
            StringComparer.OrdinalIgnoreCase) &&
        left.DisableBuiltinMcps == right.DisableBuiltinMcps &&
        left.NoCustomInstructions == right.NoCustomInstructions;

    private static SessionRuntimeConfigurationException WrapConfigurationFailure(
        string message,
        string sessionId,
        Exception ex) =>
        new(message, ex)
        {
            SessionId = sessionId,
        };

    private static async Task DisposeAttachedAsync(AttachedSession attached)
    {
        await attached.Session.DisposeAsync();
        await DisposeAttachedStateAsync(attached);
    }

    private static Task DisposeAttachedStateAsync(AttachedSession attached)
    {
        attached.Subscription?.Dispose();
        attached.Lifetime.Cancel();
        attached.Lifetime.Dispose();
        return Task.CompletedTask;
    }

    private sealed class AttachedSession(
        CopilotSession session,
        ISdkClientHost host,
        SessionRuntimeProfile profile,
        CancellationTokenSource lifetime)
    {
        public CopilotSession Session { get; } = session;
        public ISdkClientHost Host { get; } = host;
        public SessionRuntimeProfile Profile { get; set; } = profile;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public IDisposable? Subscription { get; set; }
    }

    private sealed class ActiveTurn(
        string? requester,
        DateTimeOffset startedAt,
        SemaphoreSlim gate)
    {
        public object Sync { get; } = new();
        public string? Requester { get; } = requester;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LastEventAt { get; set; } = startedAt;
        public SemaphoreSlim Gate { get; } = gate;
        public SdkTurnEventMapper Mapper { get; } = new();
        public HashSet<string> OpenToolCalls { get; } =
            new(StringComparer.Ordinal);
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}

#pragma warning restore GHCP001
