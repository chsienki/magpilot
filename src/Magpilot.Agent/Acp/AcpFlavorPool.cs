namespace Magpilot.Agent.Acp;

/// <summary>
/// Lazy pool of <see cref="AcpClient"/> instances, one per <see cref="AcpFlavor"/>
/// key for multiplexing flavors (e.g. default Copilot). Non-multiplexing
/// flavors (e.g. agency) get a fresh child per call to
/// <see cref="StartFreshAsync"/> instead.
///
/// All clients share one <see cref="OnSessionUpdate"/> stream and one
/// <see cref="OnRequest"/> handler -- the session manager doesn't care which
/// child raised an event, just which sessionId it was for.
/// </summary>
public sealed class AcpFlavorPool(ILoggerFactory loggerFactory, ILogger<AcpFlavorPool> log)
{
    private readonly Dictionary<string, AcpClient> _clients = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<int> _driftWarned = new();

    public event Action<string, System.Text.Json.Nodes.JsonNode?>? OnSessionUpdate;
    public event Func<string, System.Text.Json.Nodes.JsonNode?, Task<System.Text.Json.Nodes.JsonNode>>? OnRequest;

    /// <summary>
    /// Pre-register an externally-started client (used by <c>AcpStarter</c>
    /// for the eagerly-spawned default flavor).
    /// </summary>
    public async Task RegisterAsync(AcpFlavor flavor, AcpClient client)
    {
        await _lock.WaitAsync();
        try
        {
            client.OnSessionUpdate += FanoutSessionUpdate;
            client.OnRequest += FanoutRequest;
            _clients[flavor.Key] = client;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// For multiplexing flavors: returns the existing client (if its
    /// subprocess is still alive) or starts a new one and caches it.
    /// For non-multiplexing flavors: always starts a fresh child.
    /// </summary>
    /// <remarks>
    /// Liveness check: if the cached client's <c>copilot --acp</c>
    /// subprocess has exited (crashed, killed, OOM'd...), the dead
    /// instance is disposed and a fresh one spawned in its place.
    /// Without this check we would keep handing out dead clients
    /// forever; every <see cref="AcpClient.CallAsync"/> would write
    /// into a broken pipe and time out 120s later -- the symptom that
    /// surfaces in the SPA as "new session never finishes" and
    /// breaks Preflight context discovery (which routes through
    /// <c>session/new</c>).
    /// </remarks>
    public async Task<AcpClient> AcquireAsync(AcpFlavor flavor, CancellationToken ct)
    {
        if (!flavor.MultiplexesSessions)
        {
            // Per-session child; no caching, no shared lock contention.
            return await StartFreshAsync(flavor, ct);
        }

        await _lock.WaitAsync(ct);
        try
        {
            if (_clients.TryGetValue(flavor.Key, out var existing))
            {
                if (existing.IsAlive)
                {
                    WarnIfBinaryDrifted(existing);
                    return existing;
                }

                log.LogWarning(
                    "Cached ACP client for flavor {Flavor} is dead -- respawning",
                    flavor.Key);
                _clients.Remove(flavor.Key);
                try { await existing.DisposeAsync(); }
                catch (Exception ex) { log.LogDebug(ex, "Disposing dead ACP client threw"); }
            }
            var client = await StartFreshAsync(flavor, ct);
            _clients[flavor.Key] = client;
            return client;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Dispose the cached client for a multiplexing flavor and spawn a fresh one
    /// in its place, returning the replacement. Used to clear a stale-resume: a
    /// long-lived ACP child serves a frozen in-memory snapshot of a session that
    /// another process advanced on disk, and copilot's <c>session/load</c> won't
    /// re-read disk for an already-loaded session. Killing the child is the only
    /// way to make it release the session so the replacement can load current
    /// state. This drops every OTHER session the child was multiplexing -- the
    /// caller is responsible for re-mapping/reloading those.
    /// </summary>
    public async Task<AcpClient> RecycleAsync(AcpFlavor flavor, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_clients.Remove(flavor.Key, out var existing))
            {
                log.LogWarning("Recycling ACP child for flavor {Flavor} (pid={Pid}) to clear stale state",
                    flavor.Key, existing.ProcessId?.ToString() ?? "?");
                try { await existing.DisposeAsync(); }
                catch (Exception ex) { log.LogDebug(ex, "Disposing recycled ACP client threw"); }
            }
            var fresh = await StartFreshAsync(flavor, ct);
            _clients[flavor.Key] = fresh;
            return fresh;
        }
        finally { _lock.Release(); }
    }

    // Warn once per drifted child: a long-lived ACP child that launched from a
    // copilot binary since replaced on disk (an in-place upgrade) keeps serving
    // the old image. Restarting the agent is the fix; surfacing it turns a
    // silent multi-week drift into a visible signal in the central log.
    private void WarnIfBinaryDrifted(AcpClient client)
    {
        if (!client.IsBinaryStale) return;
        var pid = client.ProcessId ?? -1;
        lock (_driftWarned)
        {
            if (!_driftWarned.Add(pid)) return;
        }
        log.LogWarning(
            "ACP child pid={Pid} is serving a replaced binary ({Exe}) -- copilot was upgraded on disk since " +
            "this child launched. Restart the agent to pick up the new build; a long-lived child otherwise " +
            "drifts from the on-disk world (this is how a session ended up served by copilot.exe.old).",
            pid, client.LaunchedExe);
    }

    private async Task<AcpClient> StartFreshAsync(AcpFlavor flavor, CancellationToken ct)
    {
        log.LogInformation("Spawning ACP child for flavor {Flavor}: {Exe} {Args}",
            flavor.Key, flavor.Exe, flavor.Args);
        var client = new AcpClient(loggerFactory.CreateLogger<AcpClient>(), flavor.Exe, flavor.Args);
        client.OnSessionUpdate += FanoutSessionUpdate;
        client.OnRequest += FanoutRequest;
        await client.StartAsync(ct);
        return client;
    }

    private void FanoutSessionUpdate(string sid, System.Text.Json.Nodes.JsonNode? update) =>
        OnSessionUpdate?.Invoke(sid, update);

    private async Task<System.Text.Json.Nodes.JsonNode> FanoutRequest(string method, System.Text.Json.Nodes.JsonNode? @params)
    {
        var handler = OnRequest;
        if (handler is null) return new System.Text.Json.Nodes.JsonObject();
        return await handler(method, @params);
    }
}
