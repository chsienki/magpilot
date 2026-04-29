using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Magpilot.Agent.Discovery;

/// <summary>
/// Listens for UDP broadcast probes on port 47823 and replies with this agent's
/// name + HTTP URL. Hub sends the probe; any agent on the LAN answers.
/// </summary>
public sealed class DiscoveryResponder : BackgroundService
{
    private const int DiscoveryPort = 47823;
    private const string Magic = "MAGPILOT-DISCOVER-v1";

    private readonly ILogger<DiscoveryResponder> _logger;
    private readonly IConfiguration _config;

    public DiscoveryResponder(ILogger<DiscoveryResponder> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
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

                var url = ResolveSelfUrl(result.RemoteEndPoint.Address);
                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    magic = Magic,
                    name = Environment.MachineName,
                    url,
                    os = Environment.OSVersion.VersionString,
                });
                await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint);
                _logger.LogDebug("Replied to probe from {From} with url {Url}", result.RemoteEndPoint, url);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DiscoveryResponder crashed (port {Port} likely in use)", DiscoveryPort);
        }
    }

    private string ResolveSelfUrl(IPAddress remoteHubAddr)
    {
        // 1) Explicit override via config / env wins.
        var explicitUrl = _config["Agent:PublicUrl"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_AGENT_PUBLIC_URL");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl;

        var port = _config.GetValue("Agent:Port", 5099);

        // 2) Route-based: ask the OS which local IP would be used to talk back to the hub.
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remoteHubAddr, 65530));
            if (socket.LocalEndPoint is IPEndPoint local && !IPAddress.IsLoopback(local.Address))
                return $"http://{local.Address}:{port}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Route-based local IP detection failed; falling back to interface scan");
        }

        // 3) Pick first physical, up, non-loopback IPv4.
        var fallback = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

        return fallback is not null
            ? $"http://{fallback}:{port}"
            : $"http://localhost:{port}";
    }
}
