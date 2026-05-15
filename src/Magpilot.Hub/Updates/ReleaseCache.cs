using Magpilot.Shared;

namespace Magpilot.Hub.Updates;

/// <summary>
/// Singleton cache for the hub's view of the latest published release.
/// Populated by <see cref="ReleaseTracker"/> on a 1h timer; served back
/// out via <c>GET /api/agent-version</c>.
///
/// <para>
/// Holds null until the first successful poll. <c>GET /api/agent-version</c>
/// substitutes a sane default in that case so callers always get a 200.
/// </para>
///
/// <para>
/// <see cref="UpdateAvailable"/> on the cached record is always false --
/// the endpoint computes "is the caller behind?" per request from the
/// caller's reported version.
/// </para>
/// </summary>
public sealed class ReleaseCache
{
    private volatile LatestVersionInfo? _value;

    public LatestVersionInfo? Get() => _value;

    public void Set(LatestVersionInfo value) => _value = value;
}
