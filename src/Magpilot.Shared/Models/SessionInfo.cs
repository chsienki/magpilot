namespace Magpilot.Shared.Models;

/// <summary>
/// Disposition of a Copilot CLI session on a host:
/// - <see cref="Owned"/>: held by THIS magpilot agent's copilot --acp child;
///   we can drive it directly via session/prompt.
/// - <see cref="Locked"/>: an inuse.<PID>.lock exists pointing at a live process
///   that ISN'T ours (e.g. a terminal copilot session). Adopting requires
///   killing that process first then session/load.
/// - <see cref="Dormant"/>: no live lock, free to session/load. (Renamed from
///   "Past" -- still includes any session whose lock PID is dead.)
///
/// Wire format is the integer ordinal -- preserve order on rename.
/// </summary>
public enum SessionState
{
    Owned,
    Locked,
    Dormant,
}

public sealed record SessionInfo(
    string Id,
    SessionState State,
    string? Cwd,
    string? Repository,
    string? Branch,
    string? Summary,
    int? OwnerPid,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt
);
