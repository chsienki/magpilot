namespace Magpilot.Shared.Models;

using System.Text.Json.Serialization;

/// <summary>
/// V3 of magpilot-pairing (interactive UDP discovery + adopt-in-SPA).
///
/// The agent generates a random claim secret, broadcasts a UDP query
/// to find hubs on the LAN, picks one, and POSTs
/// <see cref="PairingClaimRequest"/> to its
/// <c>POST /api/enroll/claim</c> endpoint. The hub stores
/// <c>SHA256(secret)</c> + agent name + a short fingerprint
/// (last 6 chars of the secret -- public, low-entropy hint the user
/// visually confirms) with a 5-minute TTL. Returns
/// <see cref="PairingClaimResponse"/> with a URL the launcher opens
/// in the user's browser. The admin sees the pending claim in the
/// SPA, visually compares the fingerprint to what the launcher
/// printed, clicks Adopt. Approval mints a per-agent token (same
/// machinery as the V2a voucher redeem); the agent's long-poll
/// against <c>GET /api/enroll/claim-status</c> picks it up + writes
/// magpilot.env.
///
/// The shape is essentially a voucher with the secret-generation
/// direction reversed: V2a vouchers are hub-generated + agent-redeemed;
/// V3 claims are agent-generated + admin-approved. Both end at the
/// same place (per-agent token written to agents.token in the hub DB
/// and to MAGPILOT_AGENT_TOKEN in magpilot.env on the agent host).
/// </summary>
public sealed record PairingClaimRequest(string Secret, string AgentName);

/// <summary>
/// Hub's response to <see cref="PairingClaimRequest"/>. The launcher
/// uses <see cref="ApproveUrl"/> to open the admin's browser at the
/// right page, and prints <see cref="Fingerprint"/> so the admin can
/// visually verify they're approving the correct agent. <see cref="ClaimId"/>
/// is the server-side identifier the admin endpoints reference; the
/// agent never needs it (the claim secret is its handle for
/// <see cref="GetClaimStatus"/>).
/// </summary>
public sealed record PairingClaimResponse(int ClaimId, string ApproveUrl, string Fingerprint);

/// <summary>
/// Lifecycle states for a pairing claim, projected to the
/// long-poll wire.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PairingClaimState>))]
public enum PairingClaimState
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

/// <summary>
/// Long-poll response for <c>GET /api/enroll/claim-status</c>.
/// <see cref="AgentToken"/> is populated only when <see cref="Status"/>
/// is <see cref="PairingClaimState.Approved"/>; the launcher writes
/// it into <c>MAGPILOT_AGENT_TOKEN</c> and bounces the scheduled task.
/// </summary>
public sealed record PairingClaimStatus(PairingClaimState Status, string? AgentToken);

/// <summary>
/// Admin-list projection: what the SPA's "Pending pair requests"
/// section renders for each claim. Includes the fingerprint so the
/// admin can visually compare it against what the launcher console
/// printed on the agent host.
/// </summary>
public sealed record PairingClaimSummary(
    int Id,
    string AgentName,
    string Fingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    PairingClaimState Status);
