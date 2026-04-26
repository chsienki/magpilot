namespace Clawpilot.Shared.Models;

public enum SessionState
{
    LiveOwned,
    LiveOrphan,
    Past,
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
