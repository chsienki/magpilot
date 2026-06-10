using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magpilot.Shared.Models;

namespace Magpilot.Host;

/// <summary>
/// V3 of magpilot-pairing: <c>magpilot --magpilot-pair</c> with no
/// bundle argument enters interactive discovery mode.
///
/// 1. Broadcast <c>MAGPILOT-PAIR-DISCOVER-v1</c> on UDP/47824.
/// 2. Collect hub replies for ~3 seconds.
/// 3. If multiple hubs: prompt the user to pick one. Single hub:
///    auto-pick.
/// 4. Generate a 32-byte CSPRNG claim secret.
/// 5. POST <c>/api/enroll/claim</c> against the chosen hub with the
///    secret + agent name (machine hostname). Get back a claim id +
///    fingerprint + approve URL.
/// 6. Print the fingerprint so the user can visually compare it
///    against the SPA. Open the approve URL in the user's browser.
/// 7. Long-poll <c>/api/enroll/claim-status</c> until status is
///    Approved (write magpilot.env + bounce task) or Rejected /
///    Expired (print error + exit non-zero).
///
/// All env-file-write + scheduled-task-bounce logic is shared with
/// the V2a bundle path via <see cref="MagpilotPairWriter"/>.
/// </summary>
internal static class MagpilotPairDiscover
{
    private const int DiscoveryPort = 47824;
    private const string DiscoveryMagic = "MAGPILOT-PAIR-DISCOVER-v1";
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    public static async Task<int> RunAsync()
    {
        var agentName = ResolveAgentName();
        Console.WriteLine($"magpilot pair: searching for hubs on the LAN (agent name: {agentName})...");

        var hubs = await DiscoverHubsAsync(DiscoveryWindow);
        if (hubs.Count == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("No hubs found.");
            Console.Error.WriteLine("Either no magpilot hub is reachable from this machine, or the hub's");
            Console.Error.WriteLine("UDP responder isn't running. You can also pair manually with:");
            Console.Error.WriteLine("  magpilot --magpilot-pair=<bundle>");
            Console.Error.WriteLine("where <bundle> comes from the hub's /admin/enroll page.");
            return 4;
        }

        var hub = hubs.Count == 1 ? hubs[0] : PromptHubChoice(hubs);
        if (hub is null) return 0; // user cancelled
        Console.WriteLine();
        Console.WriteLine($"Pairing with: {hub.HubName} at {hub.HubUrl}");

        var secret = GenerateSecret();
        PairingClaimResponse claim;
        try
        {
            claim = await SubmitClaimAsync(hub.HubUrl, secret, agentName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not submit pairing claim: {ex.Message}");
            return 5;
        }

        Console.WriteLine();
        Console.WriteLine($"  Fingerprint: {claim.Fingerprint}");
        Console.WriteLine($"  Approve URL: {claim.ApproveUrl}");
        Console.WriteLine();
        Console.WriteLine("Compare the fingerprint above with what the hub's SPA shows,");
        Console.WriteLine("then click Adopt. Opening the approval page in your browser now...");
        TryOpenBrowser(claim.ApproveUrl);

        Console.WriteLine();
        Console.WriteLine($"Waiting for approval (timeout {ClaimTimeout.TotalMinutes:0} min). Press Ctrl+C to cancel.");

        string? agentToken;
        try
        {
            agentToken = await PollUntilDecidedAsync(hub.HubUrl, secret, ClaimTimeout);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Polling claim status failed: {ex.Message}");
            return 6;
        }

        if (agentToken is null)
        {
            // Rejected or expired -- specific message already
            // printed by PollUntilDecidedAsync.
            return 7;
        }

        // Same env-write + task-bounce as the V2a bundle path.
        var envPath = MagpilotPairWriter.ResolveEnvPath();
        Console.WriteLine($"Writing {envPath}");
        try
        {
            MagpilotPairWriter.UpsertEnv(envPath, new Dictionary<string, string>
            {
                ["MAGPILOT_HUB_URL"]     = hub.HubUrl,
                ["MAGPILOT_AGENT_TOKEN"] = agentToken,
                ["MAGPILOT_HUB_BEARER"]  = "", // V3: no bundle so no hub bearer
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not write {envPath}: {ex.Message}");
            return 3;
        }

        var usedExplicitEnvFile = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("MAGPILOT_ENV_FILE"));
        if (usedExplicitEnvFile)
        {
            Console.WriteLine("MAGPILOT_ENV_FILE override set; not touching the installed scheduled task.");
        }
        else if (OperatingSystem.IsWindows())
        {
            await MagpilotPairWriter.TryRestartScheduledTaskAsync();
        }

        Console.WriteLine();
        Console.WriteLine($"Paired {agentName} with {hub.HubUrl}");
        return 0;
    }

    private sealed record DiscoveredHub(string HubName, string HubUrl);

    private static async Task<List<DiscoveredHub>> DiscoverHubsAsync(TimeSpan window)
    {
        var hubs = new Dictionary<string, DiscoveredHub>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient { EnableBroadcast = true };
        // Bind to an ephemeral port so we can receive unicast replies.
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var payload = Encoding.UTF8.GetBytes(DiscoveryMagic);

        // Broadcast a few times in case packets are dropped or the hub
        // is mid-cycle. 3 broadcasts spread across the window is enough
        // for a quiet LAN.
        var broadcasts = 3;
        var perBroadcast = window / broadcasts;

        using var windowCts = new CancellationTokenSource(window);
        var listener = Task.Run(async () =>
        {
            try
            {
                while (!windowCts.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(windowCts.Token);
                    try
                    {
                        var reply = JsonSerializer.Deserialize<DiscoveryReply>(result.Buffer);
                        if (reply?.Magic == DiscoveryMagic
                            && !string.IsNullOrEmpty(reply.HubUrl)
                            && !string.IsNullOrEmpty(reply.HubName))
                        {
                            hubs[reply.HubUrl] = new DiscoveredHub(reply.HubName, reply.HubUrl);
                        }
                    }
                    catch { /* malformed reply, ignore */ }
                }
            }
            catch (OperationCanceledException) { }
        }, windowCts.Token);

        for (var i = 0; i < broadcasts; i++)
        {
            try
            {
                await udp.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  (broadcast {i + 1}/{broadcasts} failed: {ex.Message})");
            }
            if (i < broadcasts - 1)
                await Task.Delay(perBroadcast);
        }

        try { await listener; } catch { /* expected on cancel */ }
        return hubs.Values.ToList();
    }

    private sealed record DiscoveryReply(
        [property: JsonPropertyName("magic")] string Magic,
        [property: JsonPropertyName("hubUrl")] string HubUrl,
        [property: JsonPropertyName("hubName")] string HubName);

    private static DiscoveredHub? PromptHubChoice(List<DiscoveredHub> hubs)
    {
        Console.WriteLine();
        Console.WriteLine("Found multiple hubs:");
        for (var i = 0; i < hubs.Count; i++)
            Console.WriteLine($"  [{i + 1}] {hubs[i].HubName} ({hubs[i].HubUrl})");
        Console.WriteLine($"  [c] cancel");
        while (true)
        {
            Console.Write("Pick one: ");
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line)) continue;
            line = line.Trim().ToLowerInvariant();
            if (line == "c" || line == "cancel") return null;
            if (int.TryParse(line, out var n) && n >= 1 && n <= hubs.Count)
                return hubs[n - 1];
            Console.WriteLine("  (not a valid choice; type the number or 'c')");
        }
    }

    private static async Task<PairingClaimResponse> SubmitClaimAsync(string hubUrl, string secret, string agentName)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var url = hubUrl.TrimEnd('/') + "/api/enroll/claim";
        var resp = await http.PostAsJsonAsync(url, new PairingClaimRequest(secret, agentName));
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"{(int)resp.StatusCode} {resp.StatusCode}: {body}");
        }
        var claim = await resp.Content.ReadFromJsonAsync<PairingClaimResponse>();
        return claim ?? throw new InvalidOperationException("hub returned empty claim response");
    }

    private static async Task<string?> PollUntilDecidedAsync(string hubUrl, string secret, TimeSpan timeout)
    {
        using var deadlineCts = new CancellationTokenSource(timeout);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var url = hubUrl.TrimEnd('/') + $"/api/enroll/claim-status?secret={Uri.EscapeDataString(secret)}";

        while (!deadlineCts.IsCancellationRequested)
        {
            try
            {
                var status = await http.GetFromJsonAsync<PairingClaimStatus>(url, deadlineCts.Token);
                if (status is null)
                {
                    // Empty body -- treat as transient, re-poll after a tiny pause.
                    await Task.Delay(TimeSpan.FromSeconds(2), deadlineCts.Token);
                    continue;
                }

                switch (status.Status)
                {
                    case PairingClaimState.Approved:
                        Console.WriteLine("Approved.");
                        return status.AgentToken;
                    case PairingClaimState.Rejected:
                        Console.Error.WriteLine("Pairing rejected by the admin.");
                        return null;
                    case PairingClaimState.Expired:
                        Console.Error.WriteLine("Pairing claim expired (admin didn't decide within the 5-minute window).");
                        return null;
                    case PairingClaimState.Pending:
                    default:
                        // Long-poll returned without a decision -- re-poll.
                        continue;
                }
            }
            catch (OperationCanceledException)
            {
                // Either our deadline fired or the user Ctrl+C'd.
                Console.Error.WriteLine("Polling cancelled.");
                return null;
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"  (poll error: {ex.Message}; retrying)");
                try { await Task.Delay(TimeSpan.FromSeconds(2), deadlineCts.Token); }
                catch (OperationCanceledException) { return null; }
            }
        }

        Console.Error.WriteLine("Pairing window closed (no decision within timeout).");
        return null;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (couldn't auto-open browser: {ex.Message})");
            Console.WriteLine($"  Open this URL manually: {url}");
        }
    }

    private static string GenerateSecret()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string ResolveAgentName() =>
        Environment.GetEnvironmentVariable("MAGPILOT_AGENT_NAME") is { Length: > 0 } n
            ? n
            : Environment.MachineName;
}
