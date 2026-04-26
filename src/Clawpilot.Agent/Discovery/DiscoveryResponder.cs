using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Clawpilot.Agent.Discovery;

/// <summary>
/// Listens for UDP broadcast probes on port 47823 and replies with this agent's
/// name + HTTP URL. Hub sends the probe; any agent on the LAN answers.
/// </summary>
public sealed class DiscoveryResponder : BackgroundService
{
    private const int DiscoveryPort = 47823;
    private const string Magic = "CLAWPILOT-DISCOVER-v1";

    private readonly ILogger<DiscoveryResponder> _logger;
    private readonly IServer _server;

    public DiscoveryResponder(ILogger<DiscoveryResponder> logger, IServer server)
    {
        _logger = logger;
        _server = server;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _logger.LogInformation("Discovery responder listening on UDP/{Port}", DiscoveryPort);
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                var msg = System.Text.Encoding.UTF8.GetString(result.Buffer);
                if (!msg.StartsWith(Magic, StringComparison.Ordinal)) continue;

                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    magic = Magic,
                    name = Environment.MachineName,
                    url = ResolveSelfUrl(),
                    os = Environment.OSVersion.VersionString,
                });
                await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DiscoveryResponder crashed (port {Port} likely in use)", DiscoveryPort);
        }
    }

    private string ResolveSelfUrl()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>();
        var first = addresses?.Addresses.FirstOrDefault();
        return first ?? "http://localhost:5099";
    }
}
