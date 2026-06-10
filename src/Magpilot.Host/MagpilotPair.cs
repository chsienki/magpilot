using System.Diagnostics;
using System.Net.Http.Json;
using Magpilot.Shared.Models;

namespace Magpilot.Host;

/// <summary>
/// <c>magpilot --magpilot-pair=&lt;bundle&gt;</c> runner (V2a).
///
/// Decodes a <c>magpilot2+</c> bundle issued by the hub's
/// <c>/admin/enroll</c> page, redeems the embedded voucher against
/// <c>POST /api/enroll/redeem</c> to mint a per-agent token, and
/// writes the three secrets the agent needs
/// (<c>MAGPILOT_HUB_URL</c>, <c>MAGPILOT_AGENT_TOKEN</c>,
/// <c>MAGPILOT_HUB_BEARER</c>) into <c>magpilot.env</c>. Restarts
/// the installed <c>MagpilotAgent</c> scheduled task so the new
/// values take effect.
///
/// Backwards compat with V1 <c>magpilot1+</c> bundles is gone --
/// <see cref="EnrollmentBundle.TryDecode"/> only accepts the V2
/// prefix.
/// </summary>
internal static class MagpilotPair
{
    /// <summary>The three keys this runner writes. Anything else in the env file is preserved.</summary>
    private static readonly string[] ManagedKeys =
    [
        "MAGPILOT_HUB_URL",
        "MAGPILOT_AGENT_TOKEN",
        "MAGPILOT_HUB_BEARER",
    ];

    public static async Task<int> RunAsync(string encoded)
    {
        if (!EnrollmentBundle.TryDecode(encoded, out var bundle, out var error))
        {
            Console.Error.WriteLine($"magpilot --magpilot-pair: {error}");
            return 2;
        }

        // Redeem the voucher against the hub before touching disk --
        // a failed redeem (expired / already consumed / invalid) is
        // the most likely failure mode and we'd rather not half-update
        // magpilot.env in that case. The successful redeem returns
        // the freshly-minted per-agent bearer.
        var agentName = ResolveAgentName();
        Console.WriteLine($"Pairing as {agentName} against {bundle!.HubUrl}...");
        string agentToken;
        try
        {
            agentToken = await RedeemVoucherAsync(bundle.HubUrl, bundle.Voucher, agentName);
        }
        catch (PairingException ex)
        {
            Console.Error.WriteLine($"magpilot --magpilot-pair: {ex.Message}");
            return ex.ExitCode;
        }

        var envPath = MagpilotPairWriter.ResolveEnvPath();
        Console.WriteLine($"Writing {envPath}");
        try
        {
            MagpilotPairWriter.UpsertEnv(envPath, new Dictionary<string, string>
            {
                ["MAGPILOT_HUB_URL"]     = bundle.HubUrl,
                ["MAGPILOT_AGENT_TOKEN"] = agentToken,
                ["MAGPILOT_HUB_BEARER"]  = bundle.HubBearer,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot --magpilot-pair: could not write {envPath}: {ex.Message}");
            return 3;
        }

        // Bounce the scheduled task so the agent picks up the new
        // values. Skipped when MAGPILOT_ENV_FILE points at a custom
        // location (dev / test scenario where we'd otherwise stomp on
        // an unrelated installed agent).
        var usedExplicitEnvFile = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("MAGPILOT_ENV_FILE"));
        if (usedExplicitEnvFile)
        {
            Console.WriteLine("MAGPILOT_ENV_FILE override set; not touching the installed scheduled task. Restart the affected agent manually if it's running.");
        }
        else if (OperatingSystem.IsWindows())
        {
            await MagpilotPairWriter.TryRestartScheduledTaskAsync();
        }
        else
        {
            Console.WriteLine("Restart the magpilot agent manually so the new values take effect.");
        }

        Console.WriteLine();
        Console.WriteLine($"Paired {agentName} with {bundle.HubUrl}");
        Console.WriteLine($"  Env file: {envPath}");
        return 0;
    }

    /// <summary>
    /// POST the voucher to the hub's redeem endpoint. Translates the
    /// non-200 statuses into <see cref="PairingException"/> with a
    /// useful message; 200 returns the minted agent token.
    /// </summary>
    private static async Task<string> RedeemVoucherAsync(string hubUrl, string voucher, string agentName)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var url = hubUrl.TrimEnd('/') + "/api/enroll/redeem";
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync(url, new EnrollmentRedeemRequest(voucher, agentName));
        }
        catch (Exception ex)
        {
            throw new PairingException($"could not reach hub at {hubUrl}: {ex.Message}", exitCode: 4);
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadFromJsonAsync<EnrollmentRedeemResponse>();
            if (body is null || string.IsNullOrWhiteSpace(body.AgentToken))
                throw new PairingException("hub returned 200 but no agentToken in the body", exitCode: 5);
            return body.AgentToken;
        }

        // Pull the error text out of the body if it's JSON; otherwise
        // fall back to the status reason. The hub emits
        // { "error": "..." } for the redeem failures we care about.
        string detail = resp.ReasonPhrase ?? resp.StatusCode.ToString();
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorBody>();
            if (err?.Error is { Length: > 0 } e) detail = e;
        }
        catch { /* malformed body -- keep the status reason */ }

        var hint = resp.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                $"voucher rejected: {detail}. Generate a fresh one on the hub's /admin/enroll page.",
            System.Net.HttpStatusCode.Gone =>
                $"voucher unusable: {detail}. Generate a fresh one on the hub's /admin/enroll page.",
            System.Net.HttpStatusCode.BadRequest =>
                $"bad redeem request: {detail}",
            _ => $"redeem failed with {(int)resp.StatusCode} {resp.StatusCode}: {detail}",
        };
        throw new PairingException(hint, exitCode: 6);
    }

    private sealed record ErrorBody(string? Error);

    private sealed class PairingException(string message, int exitCode) : Exception(message)
    {
        public int ExitCode { get; } = exitCode;
    }

    private static string ResolveAgentName() =>
        Environment.GetEnvironmentVariable("MAGPILOT_AGENT_NAME") is { Length: > 0 } n
            ? n
            : Environment.MachineName;
}

