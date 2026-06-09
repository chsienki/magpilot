using System.Diagnostics;
using Magpilot.Shared.Models;

namespace Magpilot.Host;

/// <summary>
/// <c>magpilot --magpilot-pair=&lt;bundle&gt;</c> runner. Decodes a
/// bundle issued by the hub's <c>/admin/enroll</c> page, writes the
/// three secrets it carries into <c>magpilot.env</c> (preserving any
/// existing keys + comments), and restarts the installed
/// <c>MagpilotAgent</c> scheduled task so the new values take effect.
///
/// First-install flow (V1 of magpilot-pairing):
///   1. <c>irm ... | iex</c> runs the bootstrap installer.
///   2. Agent starts in "disconnected" mode -- has a random
///      <c>MAGPILOT_AGENT_TOKEN</c> from install-task.ps1 but no hub URL.
///   3. User copies the bundle from the hub's /admin/enroll page.
///   4. User runs <c>magpilot --magpilot-pair=&lt;bundle&gt;</c>.
///   5. This class overwrites <c>MAGPILOT_HUB_URL</c>,
///      <c>MAGPILOT_AGENT_TOKEN</c>, <c>MAGPILOT_HUB_BEARER</c> in
///      <c>magpilot.env</c> and restarts the agent. Done.
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

        var envPath = ResolveEnvPath();
        Console.WriteLine($"Pairing: writing {envPath}");
        try
        {
            UpsertEnv(envPath, new Dictionary<string, string>
            {
                ["MAGPILOT_HUB_URL"]     = bundle!.HubUrl,
                ["MAGPILOT_AGENT_TOKEN"] = bundle.AgentToken,
                ["MAGPILOT_HUB_BEARER"]  = bundle.HubBearer,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot --magpilot-pair: could not write {envPath}: {ex.Message}");
            return 3;
        }

        // Try to bounce the scheduled task so the agent picks up the new
        // values. Best-effort -- when running on a dev machine that
        // doesn't have the installed task (or non-Windows), this is a
        // no-op with a one-line hint.
        //
        // Skip the restart when MAGPILOT_ENV_FILE points at a custom
        // location: that's a deliberate signal that we're operating on
        // a non-installed env file (dev / test / multi-instance), so
        // bouncing the installed scheduled task would kill an
        // unrelated agent the user didn't ask us to touch.
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
        Console.WriteLine($"Paired with {bundle.HubUrl}");
        Console.WriteLine($"  Env file: {envPath}");
        return 0;
    }

    /// <summary>
    /// Locate the agent's <c>magpilot.env</c>. Same lookup chain as
    /// <c>Magpilot.Agent.EnvFileLoader</c>:
    ///   1. <c>MAGPILOT_ENV_FILE</c> env override (handy for dev).
    ///   2. <c>%ProgramFiles%\Magpilot\config\magpilot.env</c> (installer layout).
    ///   3. Fallback: same path even if it doesn't exist, so the
    ///      operation creates it -- supports the case where someone
    ///      installs magpilot then runs --magpilot-pair before the
    ///      scheduled task has ever started.
    /// </summary>
    private static string ResolveEnvPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MAGPILOT_ENV_FILE");
        if (!string.IsNullOrEmpty(explicitPath))
            return explicitPath;

        // The installer puts the launcher at <Program Files>\Magpilot\bin\magpilot.exe
        // and the env file at <Program Files>\Magpilot\config\magpilot.env, so we can
        // resolve relative to our own location.
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

        // Last-resort fallback for development runs from source.
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

        // Brief wait so the process release ports and file handles.
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
