using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Magpilot.Hub.Agents;
using Magpilot.Shared.Models;
using Microsoft.Data.Sqlite;

namespace Magpilot.Hub.Auth;

/// <summary>
/// V3 of magpilot-pairing: the agent-initiated claim flow.
///
/// The agent generates a random claim secret, POSTs it via
/// <see cref="CreateClaim"/>, and long-polls
/// <see cref="AwaitStatusAsync"/> until an admin approves or rejects
/// the claim in the SPA. The hub stores only <c>SHA256(secret)</c>;
/// the agent's secret is its handle for all subsequent polls.
///
/// Mirrors the V2a <see cref="EnrollmentService"/> shape -- same
/// hash-only storage, same atomic-consume discipline, same per-agent
/// token mintage on success -- with the secret-generation direction
/// reversed (agent-generated vs. hub-generated voucher) and an
/// admin confirmation step inserted before the token is minted.
/// </summary>
public sealed class ClaimService
{
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LongPollMax = TimeSpan.FromSeconds(60);

    private readonly AgentRegistry _registry;
    private readonly EnrollmentService _enrol;
    private readonly ILogger<ClaimService> _logger;

    /// <summary>
    /// Per-claim TaskCompletionSource that the long-poll awaits.
    /// Approve / Reject signal the TCS so the polling client wakes
    /// immediately, no extra DB roundtrip needed in the common case.
    /// Key is the integer claim id (DB primary key); cleaned up on
    /// terminal status transitions.
    /// </summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _waiters = new();

    public ClaimService(AgentRegistry registry, EnrollmentService enrol, ILogger<ClaimService> logger)
    {
        _registry = registry;
        _enrol = enrol;
        _logger = logger;
    }

    /// <summary>
    /// Create a new pending claim. Stores <c>SHA256(secret)</c> + the
    /// agent name + a 6-char fingerprint (last 6 chars of the secret)
    /// with a 5-minute TTL. Returns the claim id + the fingerprint
    /// the launcher prints for visual verification. The
    /// <paramref name="approveBaseUrl"/> is concatenated with
    /// <c>?pending=&lt;claimId&gt;</c> by the endpoint layer.
    /// </summary>
    public (int ClaimId, string Fingerprint) CreateClaim(string secret, string agentName)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("secret required", nameof(secret));
        if (string.IsNullOrWhiteSpace(agentName)) throw new ArgumentException("agentName required", nameof(agentName));

        var fingerprint = FingerprintOf(secret);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAt = nowMs + (long)ClaimTtl.TotalMilliseconds;

        using var c = new SqliteConnection(_registry.ConnStringInternal);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO claims (secret_hash, agent_name, fingerprint, created_at, expires_at, status)
            VALUES ($hash, $name, $fp, $created, $expires, 'pending');
            SELECT last_insert_rowid();
        """;
        cmd.Parameters.AddWithValue("$hash", HashSecret(secret));
        cmd.Parameters.AddWithValue("$name", agentName);
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        cmd.Parameters.AddWithValue("$created", nowMs);
        cmd.Parameters.AddWithValue("$expires", expiresAt);
        var id = Convert.ToInt32(cmd.ExecuteScalar());

        _logger.LogInformation("Pairing claim #{Id} created for agent '{Name}' (fingerprint {Fp})",
            id, agentName, fingerprint);
        return (id, fingerprint);
    }

    /// <summary>
    /// Long-poll the claim's status. Returns immediately if the claim
    /// is already in a terminal state OR if the TTL has elapsed.
    /// Otherwise blocks server-side for up to <see cref="LongPollMax"/>
    /// (60s) waiting for Approve/Reject to signal. The launcher should
    /// loop this call until status is non-Pending.
    /// </summary>
    public async Task<PairingClaimStatus> AwaitStatusAsync(string secret, CancellationToken ct)
    {
        var snapshot = LookupBySecret(secret);
        if (snapshot is null)
            return new PairingClaimStatus(PairingClaimState.Rejected, null);

        // Treat an expired pending row as terminal so the launcher
        // doesn't long-poll a dead claim. The DB transition to
        // 'expired' happens lazily on read; eventually a small sweep
        // could finalize it, but for V3 we just project at read time.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (snapshot.Value.Status == PairingClaimState.Pending && nowMs > snapshot.Value.ExpiresAt)
            return new PairingClaimStatus(PairingClaimState.Expired, null);

        if (snapshot.Value.Status != PairingClaimState.Pending)
            return new PairingClaimStatus(snapshot.Value.Status, snapshot.Value.AgentToken);

        // Park on a TCS keyed by claim id. Approve/Reject signals
        // it; the long-poll wakes immediately. Multiple concurrent
        // polls for the same claim share the same TCS.
        var tcs = _waiters.GetOrAdd(snapshot.Value.Id, _ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        using var timeoutCts = new CancellationTokenSource(LongPollMax);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var reg = linked.Token.Register(() => { /* wake the await via continuation in WhenAny */ });

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, linked.Token));
        // Either the TCS fired (status changed) or our cancellation
        // timer fired (long-poll timed out -- launcher will re-poll).
        // Re-read the DB either way to get the authoritative state.
        var fresh = LookupBySecret(secret)
            ?? new ClaimRow(snapshot.Value.Id, secret, snapshot.Value.AgentName,
                snapshot.Value.Fingerprint, snapshot.Value.CreatedAt, snapshot.Value.ExpiresAt,
                PairingClaimState.Rejected, null);

        var status = fresh.Status;
        if (status == PairingClaimState.Pending && nowMs > fresh.ExpiresAt)
            status = PairingClaimState.Expired;
        return new PairingClaimStatus(status, fresh.AgentToken);
    }

    /// <summary>
    /// Admin-approved a pending claim. Mints a per-agent token via
    /// <see cref="EnrollmentService.MintTokenForApprovedClaim"/>,
    /// updates the row + agents table atomically, signals waiters.
    /// Returns false if the claim doesn't exist or isn't pending.
    /// </summary>
    public bool ApproveClaim(int claimId, string? decidedByUser)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var c = new SqliteConnection(_registry.ConnStringInternal);
        c.Open();
        using var tx = c.BeginTransaction();

        string agentName;
        long expiresAt;
        string currentStatus;
        using (var probe = c.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT agent_name, expires_at, status FROM claims WHERE id = $id";
            probe.Parameters.AddWithValue("$id", claimId);
            using var r = probe.ExecuteReader();
            if (!r.Read()) return false;
            agentName = r.GetString(0);
            expiresAt = r.GetInt64(1);
            currentStatus = r.GetString(2);
        }
        if (currentStatus != "pending") return false;
        if (nowMs > expiresAt) return false;

        // Mint the per-agent token + upsert the agents row in the
        // same transaction as the claim status transition. Reuses
        // the V2a redeem machinery so the agents-table side stays
        // identical between voucher-redeem and claim-approve paths.
        var agentToken = _enrol.MintTokenForClaim(c, tx, agentName, claimId);

        using (var update = c.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE claims
                SET status = 'approved',
                    agent_token = $token,
                    decided_at = $now,
                    decided_by_user = $user
                WHERE id = $id
            """;
            update.Parameters.AddWithValue("$token", agentToken);
            update.Parameters.AddWithValue("$now", nowMs);
            update.Parameters.AddWithValue("$user", (object?)decidedByUser ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", claimId);
            update.ExecuteNonQuery();
        }

        tx.Commit();
        _registry.Reload(agentName);
        SignalWaiters(claimId);
        _logger.LogInformation("Pairing claim #{Id} approved by {User} -> agent {Name}",
            claimId, decidedByUser ?? "<anonymous>", agentName);
        return true;
    }

    /// <summary>Admin-rejected a pending claim. Signals waiters so the launcher's long-poll returns immediately.</summary>
    public bool RejectClaim(int claimId, string? decidedByUser)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var c = new SqliteConnection(_registry.ConnStringInternal);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE claims
            SET status = 'rejected', decided_at = $now, decided_by_user = $user
            WHERE id = $id AND status = 'pending'
        """;
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$user", (object?)decidedByUser ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", claimId);
        var changed = cmd.ExecuteNonQuery();
        if (changed == 0) return false;
        SignalWaiters(claimId);
        _logger.LogInformation("Pairing claim #{Id} rejected by {User}", claimId, decidedByUser ?? "<anonymous>");
        return true;
    }

    /// <summary>List claims for the admin UI. Includes pending + recently-decided so the SPA can render a short history.</summary>
    public IReadOnlyList<PairingClaimSummary> ListClaims(int recentDecidedLimit = 20)
    {
        using var c = new SqliteConnection(_registry.ConnStringInternal);
        c.Open();
        using var cmd = c.CreateCommand();
        // Pending always shown; decided ones limited to the most recent N
        // so the list doesn't grow unbounded over time.
        cmd.CommandText = """
            SELECT id, agent_name, fingerprint, created_at, expires_at, status
            FROM claims
            WHERE status = 'pending'
               OR id IN (SELECT id FROM claims WHERE status != 'pending' ORDER BY id DESC LIMIT $lim)
            ORDER BY id DESC
        """;
        cmd.Parameters.AddWithValue("$lim", recentDecidedLimit);
        using var r = cmd.ExecuteReader();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<PairingClaimSummary>();
        while (r.Read())
        {
            var id = r.GetInt32(0);
            var name = r.GetString(1);
            var fp = r.GetString(2);
            var created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3));
            var expires = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4));
            var rawStatus = r.GetString(5);
            var status = ParseStatus(rawStatus);
            // Project on-read expiry so the SPA never shows a "pending"
            // claim past its TTL even before the row's status column
            // gets updated.
            if (status == PairingClaimState.Pending && nowMs > r.GetInt64(4))
                status = PairingClaimState.Expired;
            list.Add(new PairingClaimSummary(id, name, fp, created, expires, status));
        }
        return list;
    }

    private void SignalWaiters(int claimId)
    {
        if (_waiters.TryRemove(claimId, out var tcs))
            tcs.TrySetResult();
    }

    private readonly record struct ClaimRow(
        int Id,
        string Secret,
        string AgentName,
        string Fingerprint,
        long CreatedAt,
        long ExpiresAt,
        PairingClaimState Status,
        string? AgentToken);

    private ClaimRow? LookupBySecret(string secret)
    {
        var hash = HashSecret(secret);
        using var c = new SqliteConnection(_registry.ConnStringInternal);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent_name, fingerprint, created_at, expires_at, status, agent_token
            FROM claims WHERE secret_hash = $hash
        """;
        cmd.Parameters.AddWithValue("$hash", hash);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ClaimRow(
            r.GetInt32(0),
            secret,
            r.GetString(1),
            r.GetString(2),
            r.GetInt64(3),
            r.GetInt64(4),
            ParseStatus(r.GetString(5)),
            r.IsDBNull(6) ? null : r.GetString(6));
    }

    private static PairingClaimState ParseStatus(string raw) => raw switch
    {
        "pending" => PairingClaimState.Pending,
        "approved" => PairingClaimState.Approved,
        "rejected" => PairingClaimState.Rejected,
        "expired" => PairingClaimState.Expired,
        _ => PairingClaimState.Rejected,
    };

    private static byte[] HashSecret(string secret) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>
    /// Last 6 chars of the secret. Visible to anyone who can sniff
    /// the UDP discovery exchange, so NOT a security boundary; just
    /// the visual handle the admin matches against what the launcher
    /// prints on the agent side.
    /// </summary>
    private static string FingerprintOf(string secret) =>
        secret.Length <= 6 ? secret : secret[^6..];
}
