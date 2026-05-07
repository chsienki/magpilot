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
        var token = _config["Hub:DefaultAgentToken"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_AGENT_TOKEN");

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProbeOnceAsync(token, stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Discovery probe failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ProbeOnceAsync(string? defaultAgentToken, CancellationToken ct)
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
                        _registry.Upsert(reply.Name, reply.Url, defaultAgentToken, online: true, flavors: reply.Flavors);
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
