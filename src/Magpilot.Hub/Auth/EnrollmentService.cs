using System.Security.Cryptography;
using Magpilot.Hub.Agents;
using Magpilot.Shared.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;

namespace Magpilot.Hub.Auth;

/// <summary>
/// V2a pairing: voucher-based enrollment.
///
/// <see cref="CreateVoucher"/> mints a short-lived single-use voucher
/// (32-byte CSPRNG secret, default 15min TTL), stores only its SHA256
/// hash in the database, and returns the <see cref="EnrollmentBundle"/>
/// the SPA displays. The bundle does NOT carry an agent token --
/// the launcher redeems the voucher against
/// <see cref="RedeemVoucher"/> and the hub mints a per-agent token
/// in response. Each agent gets its own token; revoking one doesn't
/// affect the others.
///
/// The shared <c>MAGPILOT_AGENT_TOKEN</c> bootstrap secret from V1 is
/// gone: an agent that isn't enrolled has no credentials, and the hub
/// can't talk to it. First call: redeem -> agent gets its token ->
/// hub stores hash -> subsequent calls work.
/// </summary>
public sealed class EnrollmentService
{
    private static readonly TimeSpan VoucherTtl = TimeSpan.FromMinutes(15);

    private readonly HubAuthOptions _auth;
    private readonly IConfiguration _config;
    private readonly IServer _server;
    private readonly AgentRegistry _registry;
    private readonly ILogger<EnrollmentService> _logger;

    public EnrollmentService(
        HubAuthOptions auth,
        IConfiguration config,
        IServer server,
        AgentRegistry registry,
        ILogger<EnrollmentService> logger)
    {
        _auth = auth;
        _config = config;
        _server = server;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Mint a one-time voucher + return the bundle the SPA shows.
    /// Returns null + an explanatory <paramref name="error"/> when the
    /// hub itself isn't fully configured to issue bundles (typically:
    /// missing <see cref="HubAuthOptions.PhoneBearer"/> or no
    /// resolvable public URL).
    /// </summary>
    public EnrollmentBundle? CreateVoucher(string? createdByUser, out string? error)
    {
        error = null;

        var hubUrl = ResolveHubPublicUrl();
        if (string.IsNullOrWhiteSpace(hubUrl))
        {
            error = "Hub public URL is unknown. Set MAGPILOT_HUB_PUBLIC_URL to the externally-reachable hub address (e.g. https://magpilot.home.example.com) and restart the hub.";
            return null;
        }

        var hubBearer = _auth.PhoneBearer;
        if (string.IsNullOrWhiteSpace(hubBearer) || hubBearer == "dev-bearer")
        {
            error = "MAGPILOT_HUB_BEARER is not set (or is the dev default). Generate a strong secret and set it on the hub before issuing enrollment vouchers.";
            return null;
        }

        var secret = GenerateSecret();
        var now = DateTimeOffset.UtcNow;
        var expires = now + VoucherTtl;

        using var c = new SqliteConnection(_registry.ConnStringInternal);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vouchers (secret_hash, created_at, expires_at, created_by_user)
            VALUES ($hash, $created, $expires, $user)
        """;
        cmd.Parameters.AddWithValue("$hash", HashSecret(secret));
        cmd.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$user", (object?)createdByUser ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        _logger.LogInformation("Issued voucher (TTL {Ttl}, by={User})", VoucherTtl, createdByUser ?? "<anonymous>");
        return new EnrollmentBundle(
            HubUrl: hubUrl.TrimEnd('/'),
            Voucher: secret,
            HubBearer: hubBearer);
    }

    /// <summary>
    /// Result of <see cref="RedeemVoucher"/>. <see cref="AgentToken"/> is non-null on success.
    /// </summary>
    public sealed record RedeemResult(string? AgentToken, RedeemStatus Status, string? Message);

    public enum RedeemStatus
    {
        Ok,
        InvalidVoucher,
        Expired,
        AlreadyConsumed,
    }

    /// <summary>
    /// Atomic check-and-consume. Validates the voucher's hash, expiry,
    /// and unused state inside a single transaction; on success, mints
    /// a fresh per-agent token (CSPRNG 32 bytes hex), records the agent
    /// in the registry with that token, marks the voucher consumed.
    /// </summary>
    public RedeemResult RedeemVoucher(string voucherSecret, string agentName, string? agentUrl)
    {
        if (string.IsNullOrWhiteSpace(voucherSecret) || string.IsNullOrWhiteSpace(agentName))
            return new RedeemResult(null, RedeemStatus.InvalidVoucher, "voucher or agent name missing");

        var hash = HashSecret(voucherSecret);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var c = new SqliteConnection(_registry.ConnStringInternal);
        c.Open();
        using var tx = c.BeginTransaction();

        long voucherId;
        long expiresAt;
        bool alreadyConsumed;

        using (var probe = c.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = """
                SELECT id, expires_at, consumed_at IS NOT NULL
                FROM vouchers WHERE secret_hash = $hash
            """;
            probe.Parameters.AddWithValue("$hash", hash);
            using var r = probe.ExecuteReader();
            if (!r.Read())
                return new RedeemResult(null, RedeemStatus.InvalidVoucher, "voucher does not match any issued");
            voucherId = r.GetInt64(0);
            expiresAt = r.GetInt64(1);
            alreadyConsumed = r.GetInt64(2) != 0;
        }

        if (alreadyConsumed)
            return new RedeemResult(null, RedeemStatus.AlreadyConsumed, "voucher has already been redeemed");
        if (nowMs > expiresAt)
            return new RedeemResult(null, RedeemStatus.Expired, $"voucher expired at {DateTimeOffset.FromUnixTimeMilliseconds(expiresAt):u}");

        // Mint a per-agent token. We store the token in plaintext in
        // agents.token rather than a hash because the hub uses it as a
        // bearer for outbound calls to the agent -- a hash would force
        // every call to re-derive, and that re-derivation isn't free.
        // The DB is at rest on the hub, which is the same trust boundary
        // that holds MAGPILOT_HUB_BEARER, so storing the per-agent
        // bearers plain doesn't widen the blast radius.
        var agentToken = GenerateSecret();

        using (var consume = c.CreateCommand())
        {
            consume.Transaction = tx;
            consume.CommandText = """
                UPDATE vouchers
                SET consumed_at = $now, consumed_by_agent_name = $name
                WHERE id = $id
            """;
            consume.Parameters.AddWithValue("$now", nowMs);
            consume.Parameters.AddWithValue("$name", agentName);
            consume.Parameters.AddWithValue("$id", voucherId);
            consume.ExecuteNonQuery();
        }

        // Persist the new agent row (or update an existing one for the
        // same name -- re-pair from the same machine replaces the token).
        // We deliberately don't preserve a previous URL: the agent will
        // re-announce via discovery and the URL will be refreshed there.
        // Until that happens, an empty URL means hub calls to this agent
        // would fail, but the agent itself works fine because all the
        // calls it makes (logs, /api/agent-version) are outbound.
        using (var upsert = c.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO agents (name, url, token, last_seen, enrolled_at, enrolled_via)
                VALUES ($name, $url, $token, $now / 1000, $now, $vid)
                ON CONFLICT(name) DO UPDATE SET
                    url          = CASE WHEN excluded.url = '' THEN agents.url ELSE excluded.url END,
                    token        = excluded.token,
                    last_seen    = excluded.last_seen,
                    enrolled_at  = excluded.enrolled_at,
                    enrolled_via = excluded.enrolled_via,
                    revoked_at   = NULL
            """;
            upsert.Parameters.AddWithValue("$name", agentName);
            // agents.url is NOT NULL on the schema (pre-existing
            // constraint from V1). The redeem flow doesn't take a URL --
            // discovery will populate it on the next probe -- so we
            // insert an empty string as a placeholder. The CASE in the
            // ON CONFLICT branch makes sure an existing URL isn't
            // overwritten with the empty placeholder when an agent
            // re-enrolls from the same name.
            upsert.Parameters.AddWithValue("$url", agentUrl ?? string.Empty);
            upsert.Parameters.AddWithValue("$token", agentToken);
            upsert.Parameters.AddWithValue("$now", nowMs);
            upsert.Parameters.AddWithValue("$vid", voucherId);
            upsert.ExecuteNonQuery();
        }

        tx.Commit();

        // Reload from disk so the next outbound call uses the new
        // token AND picks up the enrolled_at + revoked_at = NULL we
        // just wrote in the transaction above. Using Upsert here
        // would clobber those columns with the cached pre-redeem
        // values (the Upsert path preserves what the registry already
        // has in memory, which is stale by exactly the row we just
        // wrote).
        _registry.Reload(agentName);

        _logger.LogInformation("Voucher redeemed by agent {Name} (voucher_id={Vid})", agentName, voucherId);
        return new RedeemResult(agentToken, RedeemStatus.Ok, null);
    }

    /// <summary>
    /// V3 bridge: mint a fresh per-agent token + upsert the agents
    /// row inside the caller's transaction. Used by
    /// <c>ClaimService.ApproveClaim</c> so the claim status flip and
    /// the agent-row creation happen atomically together. Returns the
    /// minted token so the caller can store it in the claim row for
    /// the long-poll to pick up.
    /// </summary>
    /// <remarks>
    /// Note that <c>agents.enrolled_via</c> here gets the claim id,
    /// not a voucher id. The two id spaces don't share a counter so
    /// strictly speaking the column is ambiguous between V2a vouchers
    /// and V3 claims. For V3 we accept the ambiguity (the column is
    /// audit metadata; the actual security boundary is the per-agent
    /// token in <c>agents.token</c>). A future schema bump could add
    /// an <c>enrolled_via_kind</c> column if disambiguation becomes
    /// necessary.
    /// </remarks>
    public string MintTokenForClaim(SqliteConnection conn, SqliteTransaction tx, string agentName, int claimId)
    {
        var agentToken = GenerateSecret();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var upsert = conn.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = """
            INSERT INTO agents (name, url, token, last_seen, enrolled_at, enrolled_via)
            VALUES ($name, $url, $token, $now / 1000, $now, $cid)
            ON CONFLICT(name) DO UPDATE SET
                url          = CASE WHEN excluded.url = '' THEN agents.url ELSE excluded.url END,
                token        = excluded.token,
                last_seen    = excluded.last_seen,
                enrolled_at  = excluded.enrolled_at,
                enrolled_via = excluded.enrolled_via,
                revoked_at   = NULL
        """;
        upsert.Parameters.AddWithValue("$name", agentName);
        upsert.Parameters.AddWithValue("$url", string.Empty);
        upsert.Parameters.AddWithValue("$token", agentToken);
        upsert.Parameters.AddWithValue("$now", nowMs);
        upsert.Parameters.AddWithValue("$cid", claimId);
        upsert.ExecuteNonQuery();
        return agentToken;
    }

    private static string GenerateSecret()
    {
        // 32 bytes (256 bits) of CSPRNG output, base64url-encoded.
        // 43 chars; URL-safe; copy/paste friendly.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] HashSecret(string secret) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));

    /// <summary>
    /// Resolve the hub's externally-reachable URL. Preference order:
    ///   1. <c>MAGPILOT_HUB_PUBLIC_URL</c> -- explicit override, the
    ///      production NPM-fronted address (e.g.
    ///      <c>https://magpilot.home.example.com</c>).
    ///   2. The first non-wildcard listen URL from <c>IServerAddressesFeature</c>
    ///      (e.g. <c>http://192.168.1.239:7088</c>) -- the natural
    ///      "what did the hub bind to?" answer.
    ///   3. The first listen URL with wildcards rewritten to the
    ///      machine's LAN address -- gracious fallback for the
    ///      common <c>http://0.0.0.0:7088</c> dev binding.
    /// </summary>
    private string? ResolveHubPublicUrl()
    {
        var explicitUrl = _config["Hub:PublicUrl"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_HUB_PUBLIC_URL");
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl;

        var addrs = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addrs is null || addrs.Count == 0)
            return null;

        foreach (var addr in addrs)
        {
            if (!addr.Contains("0.0.0.0", StringComparison.Ordinal)
                && !addr.Contains("[::]", StringComparison.Ordinal)
                && !addr.Contains("//*", StringComparison.Ordinal))
                return addr;
        }

        var fallback = addrs.First();
        var lanIp = TryGetLanIp();
        if (lanIp is null) return fallback;
        return fallback
            .Replace("0.0.0.0", lanIp, StringComparison.Ordinal)
            .Replace("[::]", lanIp, StringComparison.Ordinal)
            .Replace("//*", $"//{lanIp}", StringComparison.Ordinal);
    }

    private static string? TryGetLanIp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

