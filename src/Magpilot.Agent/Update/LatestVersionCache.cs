using Magpilot.Shared;

namespace Magpilot.Agent.Update;

/// <summary>
/// Singleton cache of the hub-reported latest version. Populated by
/// <c>UpdatePoller</c> (added in a follow-up todo); served back out via
/// <c>GET /api/version/latest</c> so the launcher can decide whether to
/// print the upgrade banner on every invocation without ever talking
/// to GitHub directly.
///
/// <para>
/// Default value is "no update available, latest == current". This means
/// dev runs without a hub reachable get a sane no-op answer instead of
/// the launcher complaining about a missing endpoint.
/// </para>
///
/// <para>
/// Reads/writes use volatile semantics to make sure poll-time updates
/// are visible to the request thread without a lock; <see cref="LatestVersionInfo"/>
/// is a record (immutable), so swapping the reference atomically is safe.
/// </para>
/// </summary>
public sealed class LatestVersionCache
{
    private volatile LatestVersionInfo _value =
        new(Versioning.AssemblyVersion,
            MinProtocol: Versioning.ProtocolVersion,
            MaxProtocol: Versioning.ProtocolVersion,
            UpdateAvailable: false);

    public LatestVersionInfo Get() => _value;

    public void Set(LatestVersionInfo value) => _value = value;
}
