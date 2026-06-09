using System.Text;
using System.Text.Json;

namespace Magpilot.Shared.Models;

/// <summary>
/// V2 enrollment bundle: a one-time voucher the user pastes into
/// <c>magpilot --magpilot-pair=&lt;bundle&gt;</c> on a fresh agent
/// install. Unlike V1, the bundle does NOT carry an agent token --
/// the launcher redeems the voucher against the hub's
/// <c>POST /api/enroll/redeem</c> endpoint, and the hub mints a
/// per-agent token in response. This decouples enrollment from
/// long-lived shared secrets: a voucher is time-limited
/// (15min default), single-use, and the resulting per-agent token
/// can be revoked without affecting other agents.
///
/// Wire format: <c>magpilot2+&lt;base64url(JSON)&gt;</c>. V1's
/// <c>magpilot1+</c> format is no longer recognized (we control all
/// hubs + agents in deployment; backwards compat would be carrying
/// dead code).
/// </summary>
public sealed record EnrollmentBundle(
    string HubUrl,
    string Voucher,
    string HubBearer)
{
    private const string Prefix = "magpilot2+";

    /// <summary>
    /// Serialize to the wire string (<c>magpilot2+&lt;base64url&gt;</c>).
    /// </summary>
    public string Encode()
    {
        var json = JsonSerializer.Serialize(this, Json);
        return Prefix + Base64Url.Encode(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Try to parse a bundle string. Returns false for an unknown
    /// prefix (e.g. a leftover V1 <c>magpilot1+</c> bundle) or
    /// malformed payload; the caller is expected to surface a
    /// user-friendly error in either case.
    /// </summary>
    public static bool TryDecode(string? encoded, out EnrollmentBundle? bundle, out string? error)
    {
        bundle = null;
        error = null;

        if (string.IsNullOrWhiteSpace(encoded))
        {
            error = "Bundle is empty.";
            return false;
        }
        encoded = encoded.Trim();
        if (!encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = $"Unknown bundle format. Expected a string starting with '{Prefix}'. Generate a fresh bundle from the hub's /admin/enroll page.";
            return false;
        }

        try
        {
            var body = encoded[Prefix.Length..];
            var bytes = Base64Url.Decode(body);
            var json = Encoding.UTF8.GetString(bytes);
            var parsed = JsonSerializer.Deserialize<EnrollmentBundle>(json, Json);
            if (parsed is null)
            {
                error = "Bundle payload was empty.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.HubUrl)
                || string.IsNullOrWhiteSpace(parsed.Voucher)
                || string.IsNullOrWhiteSpace(parsed.HubBearer))
            {
                error = "Bundle is missing one of the required fields (hubUrl, voucher, hubBearer).";
                return false;
            }
            bundle = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not decode bundle: {ex.Message}";
            return false;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}

/// <summary>
/// Request body for <c>POST /api/enroll/redeem</c>. Sent by the
/// launcher when consuming an enrollment voucher.
/// </summary>
public sealed record EnrollmentRedeemRequest(
    string Voucher,
    string AgentName);

/// <summary>
/// Response body for <c>POST /api/enroll/redeem</c>. The hub mints
/// a fresh per-agent token in response to a successful redeem; the
/// launcher writes it into <c>MAGPILOT_AGENT_TOKEN</c> in
/// <c>magpilot.env</c>.
/// </summary>
public sealed record EnrollmentRedeemResponse(
    string AgentToken);

/// <summary>
/// Minimal base64url (RFC 4648 §5) codec. The .NET BCL has
/// <c>Convert.ToBase64String</c> but only the standard alphabet with
/// '+' and '/'; we want '-' and '_' (URL-safe) and no padding so the
/// bundle round-trips cleanly through shells, URLs, and copy/paste.
/// </summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes)
    {
        var s = Convert.ToBase64String(bytes);
        return s.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Decode(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        // base64 requires length to be a multiple of 4; pad back.
        return (b.Length % 4) switch
        {
            2 => Convert.FromBase64String(b + "=="),
            3 => Convert.FromBase64String(b + "="),
            _ => Convert.FromBase64String(b),
        };
    }
}

