using System.Reflection;

namespace Magpilot.Shared;

/// <summary>
/// Central version + protocol-version surface for magpilot.
///
/// <para>
/// The semver string ships in via the top-level VERSION file -> Directory.Build.props
/// -> AssemblyInformationalVersion. <see cref="AssemblyVersion"/> reads that
/// attribute at runtime so all three exes (Agent, Host, Hub) report the
/// same number without per-project plumbing.
/// </para>
///
/// <para>
/// <see cref="ProtocolVersion"/> is currently zero, which explicitly marks the
/// Agent, Hub, SPA, and Host wire contract as unstable. Components deploy in
/// lockstep and no backward compatibility is required while the value remains
/// zero. The first compatibility freeze will set the protocol to one; only
/// after that point will incompatible wire changes require a deliberate bump
/// and compatibility policy.
/// </para>
///
/// <para>
/// Do not increment the protocol for individual breaking changes while it is
/// zero. Zero is the unstable sentinel, not a released compatibility version.
/// </para>
/// </summary>
public static class Versioning
{
    public const int ProtocolVersion = 0;

    public static bool IsUpdateAvailable(string? currentVersion, string? latestVersion) =>
        !string.IsNullOrEmpty(currentVersion) &&
        Version.TryParse(currentVersion, out var current) &&
        Version.TryParse(latestVersion, out var latest) &&
        latest > current;

    public static string AssemblyVersion
    {
        get
        {
            var attr = typeof(Versioning).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var version = attr?.InformationalVersion ?? "0.0.0";
            // Strip the +<gitsha> suffix the SDK appends when SourceLink is on
            // so callers (and our wire DTOs) get a clean semver string.
            var plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }
}

/// <summary>
/// What an agent or host reports about itself when asked.
/// Returned from agent <c>GET /api/version</c>.
/// </summary>
public sealed record VersionInfo(
    string Version,
    int ProtocolVersion);

/// <summary>
/// What the hub knows about the latest published release. Carried back to
/// the agent (which caches it) and to the launcher (which reads it from the
/// agent on every invocation to decide whether to print the upgrade banner).
///
/// <para>
/// <see cref="UpdateAvailable"/> is computed against the requesting
/// component's current version. It is <see langword="false"/> when the
/// caller omits or supplies an invalid version.
/// </para>
/// </summary>
public sealed record LatestVersionInfo(
    string LatestVersion,
    int MinProtocol,
    int MaxProtocol,
    bool UpdateAvailable);

/// <summary>
/// An agent's running version plus the release metadata it most recently
/// received from the hub.
/// </summary>
public sealed record AgentVersionStatus(
    string Version,
    int ProtocolVersion,
    string LatestVersion,
    int MinProtocol,
    int MaxProtocol,
    bool UpdateAvailable,
    DateTimeOffset? LastCheckedAt,
    bool RefreshSupported = true);

/// <summary>
/// Hub-side result for one agent. A failed or offline agent carries an error
/// without preventing other agents from reporting successfully.
/// </summary>
public sealed record AgentVersionReport(
    string AgentName,
    AgentVersionStatus? Status,
    string? Error = null);

/// <summary>
/// Result of a hub-triggered release refresh followed by an immediate update
/// signal to every visible agent.
/// </summary>
public sealed record AgentUpdateCheckResult(
    string LatestVersion,
    DateTimeOffset CheckedAt,
    IReadOnlyList<AgentVersionReport> Agents);
