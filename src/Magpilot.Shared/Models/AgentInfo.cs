namespace Magpilot.Shared.Models;

public sealed record AgentInfo(
    string Name,
    string Url,
    bool Online,
    string? OsDescription = null,
    DateTimeOffset? LastSeen = null,
    IReadOnlyList<string>? Flavors = null
);
