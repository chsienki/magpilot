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
/// <see cref="ProtocolVersion"/> is a separate integer that bumps only when
/// the wire contract between Agent <-> Hub <-> Host changes incompatibly. It
/// gates the autoupdate path: the hub advertises a [min,max] protocol range
/// and clients refuse to update past the hub's max. This lets us patch the
/// agent freely as long as the protocol matches, while still preventing a
/// client from running ahead of the hub it's connected to.
/// </para>
///
/// <para>
/// As-of the introduction of this file, ProtocolVersion is the baseline 1.
/// Bump it deliberately on the same commit that breaks the wire contract.
/// </para>
/// </summary>
public static class Versioning
{
    public const int ProtocolVersion = 1;

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
/// component's current version when known; otherwise it's a raw
/// "is there a newer release in GitHub" answer.
/// </para>
/// </summary>
public sealed record LatestVersionInfo(
    string LatestVersion,
    int MinProtocol,
    int MaxProtocol,
    bool UpdateAvailable);
