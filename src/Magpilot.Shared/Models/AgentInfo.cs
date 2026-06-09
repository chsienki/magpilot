namespace Magpilot.Shared.Models;

public sealed record AgentInfo(
    string Name,
    string Url,
    bool Online,
    string? OsDescription = null,
    DateTimeOffset? LastSeen = null,
    IReadOnlyList<string>? Flavors = null,
    // V2b pairing: when this agent was first enrolled via voucher
    // (matches the redeem timestamp). Null on rows that pre-date
    // V2a or were re-loaded from an older hub.db.
    DateTimeOffset? EnrolledAt = null,
    // V2b pairing: non-null when the agent has been revoked. The hub
    // refuses outbound calls to revoked agents (Proxy returns 410
    // Gone); re-pairing via voucher clears this back to null.
    DateTimeOffset? RevokedAt = null
);
