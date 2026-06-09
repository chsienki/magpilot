using System.Text;
using System.Text.Json;

namespace Magpilot.Shared.Models;

/// <summary>
/// The three-secret bundle the magpilot launcher uses to "pair" a fresh
/// agent install with a hub in one paste. Returned by the hub's
/// <c>GET /api/admin/enroll/bundle</c> endpoint, consumed by the
/// launcher's <c>--magpilot-pair=&lt;bundle&gt;</c> subcommand.
///
/// Wire format: <c>magpilot1+&lt;base64url(JSON)&gt;</c>. The version
/// prefix lets the launcher reject bundles it doesn't know how to read
/// (e.g. a future <c>magpilot2+...</c> bundle that adds per-agent
/// tokens) with a clear "upgrade your launcher" message rather than
/// silently corrupting <c>magpilot.env</c>.
///
/// The agent's own public URL (<c>MAGPILOT_AGENT_PUBLIC_URL</c>) is
/// deliberately NOT in the bundle -- it's machine-specific and the
/// agent already auto-detects via <c>DiscoveryResponder.ResolveSelfUrl</c>
/// when the env var is unset.
/// </summary>
public sealed record EnrollmentBundle(
    string HubUrl,
    string AgentToken,
    string HubBearer)
{
    private const string Prefix = "magpilot1+";

    /// <summary>
    /// Serialize to the wire string (<c>magpilot1+&lt;base64url&gt;</c>).
    /// </summary>
    public string Encode()
    {
        var json = JsonSerializer.Serialize(this, Json);
        return Prefix + Base64Url.Encode(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Try to parse a bundle string. Returns false for an unknown
    /// prefix (foreign / future versions) or malformed payload; the
    /// caller is expected to surface a user-friendly error in either
    /// case.
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
            error = $"Unknown bundle format. Expected a string starting with '{Prefix}'; this might be from a newer magpilot. Upgrade with 'magpilot --magpilot-update'.";
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
                || string.IsNullOrWhiteSpace(parsed.AgentToken)
                || string.IsNullOrWhiteSpace(parsed.HubBearer))
            {
                error = "Bundle is missing one of the required fields (hubUrl, agentToken, hubBearer).";
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
