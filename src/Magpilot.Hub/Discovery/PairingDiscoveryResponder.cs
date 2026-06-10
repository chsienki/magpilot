using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Magpilot.Hub.Discovery;

/// <summary>
/// V3 of magpilot-pairing: the hub's inverse of the agent-side
/// <c>DiscoveryResponder</c>. Listens on UDP/47824 for
/// <c>MAGPILOT-PAIR-DISCOVER-v1</c> broadcasts from a launcher
/// running in interactive pair mode
/// (<c>magpilot --magpilot-pair</c> with no bundle), replies unicast
/// with the hub's public URL so the launcher can submit a claim via
/// HTTP.
///
/// Always-on. The trust gate is the admin-approve step in the SPA,
/// not the discovery step -- any process on the LAN can find out
/// "yes, there's a hub at X", but pairing only completes when a
/// signed-in user clicks Adopt against the matching pending claim.
///
/// Distinct port (47824) from the existing hub-to-agent discovery
/// (47823) so the two protocols don't collide and a hub running on
/// the same machine as an agent can listen to both independently.
/// </summary>
public sealed class PairingDiscoveryResponder : BackgroundService
{
    private const int Port = 47824;
    private const string Magic = "MAGPILOT-PAIR-DISCOVER-v1";

    private readonly ILogger<PairingDiscoveryResponder> _logger;
    private readonly IConfiguration _config;
    private readonly IServer _server;

    public PairingDiscoveryResponder(
        ILogger<PairingDiscoveryResponder> logger,
        IConfiguration config,
        IServer server)
    {
        _logger = logger;
        _config = config;
        _server = server;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            _logger.LogInformation("Pairing discovery responder listening on UDP/{Port}", Port);
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                var msg = Encoding.UTF8.GetString(result.Buffer);
                if (!msg.StartsWith(Magic, StringComparison.Ordinal)) continue;

                // Hub URL is resolved fresh on each probe so a config
                // change (MAGPILOT_HUB_PUBLIC_URL) takes effect without
                // a hub restart.
                var hubUrl = ResolveHubPublicUrl(result.RemoteEndPoint.Address);
                if (string.IsNullOrWhiteSpace(hubUrl))
                {
                    _logger.LogDebug("Ignoring pair-discover probe from {From}: hub URL unknown", result.RemoteEndPoint);
                    continue;
                }

                var reply = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    magic = Magic,
                    hubUrl,
                    hubName = Environment.MachineName,
                });
                await udp.SendAsync(reply, reply.Length, result.RemoteEndPoint);
                _logger.LogInformation("Replied to pair-discover probe from {From} with url {Url}",
                    result.RemoteEndPoint, hubUrl);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PairingDiscoveryResponder crashed (port {Port} likely in use)", Port);
        }
    }

    /// <summary>
    /// Same resolution chain as <c>EnrollmentService.ResolveHubPublicUrl</c>
    /// but with an extra fallback when no explicit URL is configured
    /// and the bound listen URL is a wildcard: use the network
    /// interface address that the probe was received on (i.e. the
    /// IP the probing agent reached us at). For LAN broadcasts, the
    /// probing agent's reach-us address IS the right value to send
    /// back -- whatever route the broadcast took, the unicast reply
    /// takes the inverse.
    /// </summary>
    private string? ResolveHubPublicUrl(IPAddress probingAgentAddr)
    {
        var explicitUrl = _config["Hub:PublicUrl"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_HUB_PUBLIC_URL");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl;

        var addrs = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addrs is null || addrs.Count == 0)
            return null;

        foreach (var addr in addrs)
        {
            if (!addr.Contains("0.0.0.0", StringComparison.Ordinal)
                && !addr.Contains("[::]", StringComparison.Ordinal)
                && !addr.Contains("//*", StringComparison.Ordinal))
                return addr;
        }

        // Wildcard binding: rewrite with the local interface address
        // that received the probe. UdpClient.ReceiveAsync doesn't
        // surface the local end's address directly, but the
        // probing agent's source address is on the same subnet as
        // the interface that received it, so we can find an IP on
        // our side that shares its first three octets.
        var fallback = addrs.First();
        var sameSubnetIp = TryGetMatchingLanIp(probingAgentAddr);
        if (sameSubnetIp is null) return fallback;
        return fallback
            .Replace("0.0.0.0", sameSubnetIp, StringComparison.Ordinal)
            .Replace("[::]", sameSubnetIp, StringComparison.Ordinal)
            .Replace("//*", $"//{sameSubnetIp}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Find a local IPv4 address on the same /24 as the probing
    /// agent. Heuristic, not RFC-correct, but good enough for the
    /// LAN-only deployments magpilot targets.
    /// </summary>
    private static string? TryGetMatchingLanIp(IPAddress remote)
    {
        try
        {
            var remoteBytes = remote.GetAddressBytes();
            if (remoteBytes.Length != 4) return null;
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var localBytes = addr.Address.GetAddressBytes();
                    if (localBytes[0] == remoteBytes[0]
                        && localBytes[1] == remoteBytes[1]
                        && localBytes[2] == remoteBytes[2])
                        return addr.Address.ToString();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
