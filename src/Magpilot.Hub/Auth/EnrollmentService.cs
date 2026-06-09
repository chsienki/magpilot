using Magpilot.Shared.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Magpilot.Hub.Auth;

/// <summary>
/// Generates the <see cref="EnrollmentBundle"/> a fresh agent install
/// needs to pair with this hub. V1 is "package the current shared
/// secrets into a single pastable string" -- no per-agent tokens,
/// no TTL, no single-use enforcement (a regenerated bundle simply
/// re-encodes the same secrets). Future V2 work mints per-agent
/// tokens and adds expiry; the bundle prefix versioning
/// (<c>magpilot1+</c>) is the seam those rollouts hang off.
/// </summary>
public sealed class EnrollmentService
{
    private readonly HubAuthOptions _auth;
    private readonly IConfiguration _config;
    private readonly IServer _server;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(
        HubAuthOptions auth,
        IConfiguration config,
        IServer server,
        ILogger<EnrollmentService> logger)
    {
        _auth = auth;
        _config = config;
        _server = server;
        _logger = logger;
    }

    /// <summary>
    /// Build a bundle from the hub's current configuration. Returns
    /// null + an explanatory <paramref name="error"/> when any of the
    /// three required values can't be resolved, so the SPA can render
    /// a configuration hint instead of a misleading bundle the user
    /// would copy and have no way to fix.
    /// </summary>
    public EnrollmentBundle? TryBuild(out string? error)
    {
        error = null;

        var hubUrl = ResolveHubPublicUrl();
        if (string.IsNullOrWhiteSpace(hubUrl))
        {
            error = "Hub public URL is unknown. Set MAGPILOT_HUB_PUBLIC_URL to the externally-reachable hub address (e.g. https://magpilot.home.example.com) and restart the hub.";
            return null;
        }

        var agentToken = _config["Hub:DefaultAgentToken"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_AGENT_TOKEN");
        if (string.IsNullOrWhiteSpace(agentToken) || agentToken == "dev-token")
        {
            error = "MAGPILOT_AGENT_TOKEN is not set (or is the dev default). Generate a strong secret and set it on the hub before issuing enrollment bundles.";
            return null;
        }

        var hubBearer = _auth.PhoneBearer;
        if (string.IsNullOrWhiteSpace(hubBearer) || hubBearer == "dev-bearer")
        {
            error = "MAGPILOT_HUB_BEARER is not set (or is the dev default). Generate a strong secret and set it on the hub before issuing enrollment bundles.";
            return null;
        }

        return new EnrollmentBundle(
            HubUrl: hubUrl.TrimEnd('/'),
            AgentToken: agentToken,
            HubBearer: hubBearer);
    }

    /// <summary>
    /// Resolve the hub's externally-reachable URL. Preference order:
    ///   1. <c>MAGPILOT_HUB_PUBLIC_URL</c> -- explicit override, the
    ///      production NPM-fronted address (e.g.
    ///      <c>https://magpilot.home.example.com</c>).
    ///   2. The first non-wildcard listen URL from <c>IServerAddressesFeature</c>
    ///      (e.g. <c>http://192.168.1.239:7088</c>) -- the natural
    ///      "what did the hub bind to?" answer.
    ///   3. The first listen URL with wildcards rewritten to the
    ///      machine's LAN address -- gracious fallback for the
    ///      common <c>http://0.0.0.0:7088</c> dev binding so the
    ///      LAN address (not "0.0.0.0") makes it into the bundle.
    /// </summary>
    private string? ResolveHubPublicUrl()
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

        // Everything was a wildcard. Rewrite the first one with the
        // primary LAN address so the bundle doesn't ship "0.0.0.0"
        // (which is meaningless to an agent on a different machine).
        var fallback = addrs.First();
        var lanIp = TryGetLanIp();
        if (lanIp is null) return fallback;
        return fallback
            .Replace("0.0.0.0", lanIp, StringComparison.Ordinal)
            .Replace("[::]", lanIp, StringComparison.Ordinal)
            .Replace("//*", $"//{lanIp}", StringComparison.Ordinal);
    }

    private static string? TryGetLanIp()
    {
        try
        {
            // Same heuristic the agent uses: enumerate the active
            // interfaces and pick the first non-loopback IPv4.
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
