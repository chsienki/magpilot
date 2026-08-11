using System.Net;
using Magpilot.Shared.Net;
using Xunit;

namespace Magpilot.Host.Tests;

/// <summary>
/// Covers the pure directed-broadcast math and the enumeration invariants.
/// The math is what makes discovery reach the real LAN on a multi-homed
/// host; the enumeration must never throw and must always include the
/// limited-broadcast fallback so behaviour is never worse than before.
/// </summary>
public sealed class BroadcastAddressesTests
{
    [Theory]
    [InlineData("192.168.1.248", "255.255.255.0", "192.168.1.255")]
    [InlineData("10.0.0.5", "255.0.0.0", "10.255.255.255")]
    [InlineData("172.18.0.1", "255.255.0.0", "172.18.255.255")]
    [InlineData("169.254.5.5", "255.255.0.0", "169.254.255.255")]
    [InlineData("192.168.1.100", "255.255.255.128", "192.168.1.127")] // /25 low half
    [InlineData("192.168.1.200", "255.255.255.128", "192.168.1.255")] // /25 high half
    [InlineData("192.168.1.1", "255.255.255.255", "192.168.1.1")]     // /32 -> itself
    public void Directed_sets_all_host_bits(string address, string mask, string expected)
    {
        var result = BroadcastAddresses.Directed(IPAddress.Parse(address), IPAddress.Parse(mask));
        Assert.Equal(IPAddress.Parse(expected), result);
    }

    [Fact]
    public void Directed_throws_on_family_mismatch()
    {
        Assert.Throws<ArgumentException>(() =>
            BroadcastAddresses.Directed(IPAddress.Parse("192.168.1.1"), IPAddress.IPv6Any));
    }

    [Fact]
    public void DiscoveryTargets_always_includes_limited_broadcast_first()
    {
        var targets = BroadcastAddresses.DiscoveryTargets();
        Assert.NotEmpty(targets);
        Assert.Equal(IPAddress.Broadcast, targets[0]);
    }

    [Fact]
    public void DiscoveryTargets_are_deduplicated()
    {
        var targets = BroadcastAddresses.DiscoveryTargets();
        var distinct = targets.Select(t => t.ToString()).Distinct().Count();
        Assert.Equal(targets.Count, distinct);
    }
}
