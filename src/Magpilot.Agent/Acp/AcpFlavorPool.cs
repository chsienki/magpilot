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
    /// For multiplexing flavors: returns the existing client or starts a new
    /// one and caches it. For non-multiplexing flavors: always starts a fresh
    /// child.
    /// </summary>
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
                return existing;
            }
            var client = await StartFreshAsync(flavor, ct);
            _clients[flavor.Key] = client;
            return client;
        }
        finally { _lock.Release(); }
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
