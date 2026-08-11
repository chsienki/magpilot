using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Magpilot.Shared.Net;

/// <summary>
/// Computes the IPv4 broadcast destinations to use for LAN discovery on a
/// multi-homed host.
///
/// Sending only to the limited broadcast address (255.255.255.255) is
/// unreliable when the host has several NICs -- a physical LAN plus the
/// virtual switches that Docker, WSL and Hyper-V create (172.x), or a
/// Proxmox LXC's docker bridges (172.18/172.19). The OS picks a SINGLE
/// egress interface for a limited broadcast from the routing table, and
/// that is frequently a virtual switch rather than the real LAN, so the
/// probe never reaches the network the hub/agent is actually on.
///
/// Sending to each up interface's DIRECTED broadcast (the interface's
/// address with all host bits set, e.g. 192.168.1.248/24 -> 192.168.1.255)
/// routes the probe out that specific interface, so the real LAN segment
/// is always covered regardless of routing-table quirks. Extra sends to
/// virtual-switch subnets are harmless (nothing listens there).
/// </summary>
public static class BroadcastAddresses
{
    /// <summary>
    /// The directed (subnet) broadcast for an address+mask: the address
    /// with all host bits set. e.g. 192.168.1.248 / 255.255.255.0 ->
    /// 192.168.1.255. Pure and platform-independent.
    /// </summary>
    public static IPAddress Directed(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != m.Length)
            throw new ArgumentException(
                $"address ({a.Length} bytes) and mask ({m.Length} bytes) must be the same family");
        var b = new byte[a.Length];
        for (var i = 0; i < a.Length; i++)
            b[i] = (byte)(a[i] | (byte)~m[i]);
        return new IPAddress(b);
    }

    /// <summary>
    /// Directed broadcast addresses for every up, non-loopback IPv4
    /// interface, plus the limited broadcast (255.255.255.255) as a
    /// belt-and-braces fallback (kept first). Deduplicated, order-stable.
    /// Never throws: an interface whose mask can't be read is skipped, and
    /// if enumeration fails entirely the caller still gets the limited
    /// broadcast so behaviour is no worse than before.
    /// </summary>
    public static IReadOnlyList<IPAddress> DiscoveryTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        var seen = new HashSet<string> { IPAddress.Broadcast.ToString() };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                IEnumerable<UnicastIPAddressInformation> unicast;
                try { unicast = nic.GetIPProperties().UnicastAddresses; }
                catch { continue; }

                foreach (var ua in unicast)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    IPAddress? mask;
                    try { mask = ua.IPv4Mask; }
                    catch { continue; } // IPv4Mask can throw on some platforms
                    if (mask is null || mask.Equals(IPAddress.Any)) continue;

                    IPAddress directed;
                    try { directed = Directed(ua.Address, mask); }
                    catch { continue; }

                    if (seen.Add(directed.ToString()))
                        targets.Add(directed);
                }
            }
        }
        catch { /* fall back to the limited broadcast already in the list */ }
        return targets;
    }
}
