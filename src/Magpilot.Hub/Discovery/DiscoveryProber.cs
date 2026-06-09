using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Magpilot.Hub.Discovery;

/// <summary>
/// Periodically broadcasts a UDP probe; agents reply with their name + URL.
/// </summary>
public sealed class DiscoveryProber : BackgroundService
{
    private const int Port = 47823;
    private const string Magic = "MAGPILOT-DISCOVER-v1";

    private readonly Agents.AgentRegistry _registry;
    private readonly ILogger<DiscoveryProber> _logger;
    private readonly IConfiguration _config;

    public DiscoveryProber(Agents.AgentRegistry registry, ILogger<DiscoveryProber> logger, IConfiguration config)
    {
        _registry = registry;
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSec = _config.GetValue("Hub:DiscoveryIntervalSec", 60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProbeOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Discovery probe failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Send a UDP broadcast and record any responding agents in the
    /// registry. V2a pairing: discovery is purely about location --
    /// we register the agent's name + URL so the hub knows where it
    /// is, but the per-agent bearer token comes from the enrollment
    /// flow (<c>POST /api/enroll/redeem</c>), not from a shared
    /// default. An agent that's been discovered but never enrolled
    /// has a null token in the registry, and outbound calls to it
    /// fail with 502 until the user pairs it.
    /// </summary>
    public async Task ProbeOnceAsync(CancellationToken ct)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var payload = Encoding.UTF8.GetBytes(Magic);
        await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, Port));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                try
                {
                    var reply = JsonSerializer.Deserialize<DiscoveryReply>(result.Buffer);
                    if (reply?.Magic == Magic && !string.IsNullOrEmpty(reply.Name) && !string.IsNullOrEmpty(reply.Url))
                    {
                        _logger.LogInformation("Discovered {Name} at {Url} (flavors: {Flavors})",
                            reply.Name, reply.Url,
                            reply.Flavors is null ? "<unspecified>" : string.Join(", ", reply.Flavors));
                        // Pass null token: the registry preserves any
                        // previously-stored per-agent token via the
                        // COALESCE in its UPSERT, so re-discovering an
                        // already-enrolled agent doesn't blow away its
                        // credentials.
                        _registry.Upsert(reply.Name, reply.Url, token: null, online: true, flavors: reply.Flavors);
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Bad discovery reply"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed record DiscoveryReply(
        [property: JsonPropertyName("magic")] string Magic,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("os")] string? Os,
        [property: JsonPropertyName("flavors")] IReadOnlyList<string>? Flavors
    );
}
