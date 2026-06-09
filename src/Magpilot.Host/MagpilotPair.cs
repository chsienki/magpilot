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

        var envPath = ResolveEnvPath();
        Console.WriteLine($"Writing {envPath}");
        try
        {
            UpsertEnv(envPath, new Dictionary<string, string>
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
            await TryRestartScheduledTaskAsync();
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

    /// <summary>
    /// The name the agent will advertise as. Matches what
    /// <c>DiscoveryResponder</c> on the agent side reports: defaults
    /// to <c>Environment.MachineName</c>, overridable via
    /// <c>MAGPILOT_AGENT_NAME</c>. Keeping the two in sync is what
    /// lets the hub correlate "this agent is enrolled as X" with
    /// "this discovery reply came from X."
    /// </summary>
    private static string ResolveAgentName() =>
        Environment.GetEnvironmentVariable("MAGPILOT_AGENT_NAME") is { Length: > 0 } n
            ? n
            : Environment.MachineName;

    /// <summary>
    /// Locate the agent's <c>magpilot.env</c>. Same lookup chain as
    /// <c>Magpilot.Agent.EnvFileLoader</c>:
    ///   1. <c>MAGPILOT_ENV_FILE</c> env override (handy for dev).
    ///   2. <c>&lt;launcher-exe&gt;/../config/magpilot.env</c> (installer layout).
    ///   3. <c>%ProgramFiles%\Magpilot\config\magpilot.env</c> (last-resort fallback).
    /// </summary>
    private static string ResolveEnvPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MAGPILOT_ENV_FILE");
        if (!string.IsNullOrEmpty(explicitPath))
            return explicitPath;

        var ourPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(ourPath))
        {
            var ourDir = Path.GetDirectoryName(ourPath);
            if (!string.IsNullOrEmpty(ourDir))
            {
                var candidate = Path.GetFullPath(Path.Combine(ourDir, "..", "config", "magpilot.env"));
                return candidate;
            }
        }

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(pf, "Magpilot", "config", "magpilot.env");
    }

    /// <summary>
    /// Read the file (if it exists), upsert each KEY in <paramref name="values"/>:
    ///   * If a non-comment KEY=... line already exists, replace it in place.
    ///   * Otherwise, append at the end under a "# pairing" section.
    /// Comments + blank lines + unrelated KEY=VALUE pairs are preserved
    /// verbatim. Writes the file atomically (temp + move) so a crash
    /// mid-write can't leave a half-overwritten env file.
    /// </summary>
    private static void UpsertEnv(string path, IReadOnlyDictionary<string, string> values)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var existing = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < existing.Count; i++)
        {
            var line = existing[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (values.TryGetValue(key, out var newVal))
            {
                existing[i] = $"{key}={newVal}";
                seenKeys.Add(key);
            }
        }

        var toAppend = values.Where(kvp => !seenKeys.Contains(kvp.Key)).ToList();
        if (toAppend.Count > 0)
        {
            if (existing.Count > 0 && existing[^1].Length > 0)
                existing.Add(string.Empty);
            existing.Add("# Added by magpilot --magpilot-pair");
            foreach (var (k, v) in toAppend)
                existing.Add($"{k}={v}");
        }

        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, existing);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Bounce the installed <c>MagpilotAgent</c> scheduled task via
    /// <c>schtasks.exe</c> so the agent re-reads the env file. We use
    /// schtasks (not Stop-ScheduledTask) because schtasks is on the
    /// stock PATH and doesn't require importing the ScheduledTasks
    /// PowerShell module.
    /// </summary>
    private static async Task TryRestartScheduledTaskAsync()
    {
        if (!await TaskExistsAsync())
        {
            Console.WriteLine("No installed MagpilotAgent task found. If you're running from source, restart the dev agent manually.");
            return;
        }

        Console.WriteLine("Restarting MagpilotAgent scheduled task...");
        var stopOk = await RunSchtasksAsync("/end", "/tn", "MagpilotAgent");
        if (!stopOk)
            Console.WriteLine("  schtasks /end MagpilotAgent failed (may not have been running). Continuing.");

        // Brief wait so the process releases ports and file handles.
        await Task.Delay(800);

        var startOk = await RunSchtasksAsync("/run", "/tn", "MagpilotAgent");
        if (!startOk)
        {
            Console.WriteLine("  schtasks /run MagpilotAgent failed. Start the task manually with:");
            Console.WriteLine("    Start-ScheduledTask -TaskName MagpilotAgent");
        }
        else
        {
            Console.WriteLine("  agent restart requested.");
        }
    }

    private static async Task<bool> TaskExistsAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                ArgumentList = { "/query", "/tn", "MagpilotAgent" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> RunSchtasksAsync(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

