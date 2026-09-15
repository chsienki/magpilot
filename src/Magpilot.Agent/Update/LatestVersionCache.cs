using Magpilot.Shared;

namespace Magpilot.Agent.Update;

/// <summary>
/// Singleton cache of the hub-reported latest release metadata. Populated by
/// <c>UpdatePoller</c>; served back out via
/// <c>GET /api/version/latest?from=&lt;launcher-version&gt;</c> so each launcher
/// can decide whether to print the upgrade banner without talking to GitHub
/// directly.
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

    public LatestVersionInfo Get(string? from = null)
    {
        var value = _value;
        return value with
        {
            UpdateAvailable = Versioning.IsUpdateAvailable(from, value.LatestVersion),
        };
    }

    public void Set(LatestVersionInfo value) =>
        _value = value with { UpdateAvailable = false };
}
