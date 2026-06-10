using System.Diagnostics;

namespace Magpilot.Host;

/// <summary>
/// Shared env-file + scheduled-task bookkeeping for both pairing
/// flows: <see cref="MagpilotPair"/> (V2a bundle paste) and
/// <see cref="MagpilotPairDiscover"/> (V3 UDP discovery). Both
/// arrive at the same point -- "we have a hub URL + agent token, write
/// magpilot.env and bounce the scheduled task" -- via different
/// credential paths.
/// </summary>
internal static class MagpilotPairWriter
{
    /// <summary>
    /// Locate the agent's <c>magpilot.env</c>. Same lookup chain as
    /// <c>Magpilot.Agent.EnvFileLoader</c>:
    ///   1. <c>MAGPILOT_ENV_FILE</c> env override (handy for dev).
    ///   2. <c>&lt;launcher-exe&gt;/../config/magpilot.env</c> (installer layout).
    ///   3. <c>%ProgramFiles%\Magpilot\config\magpilot.env</c> (last-resort fallback).
    /// </summary>
    public static string ResolveEnvPath()
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
    /// in-place replace if the key exists on a non-comment line, otherwise
    /// append at the end under a "# Added by magpilot --magpilot-pair"
    /// section. Comments + blank lines + unrelated KEY=VALUE pairs are
    /// preserved verbatim. Writes the file atomically (temp + move)
    /// so a crash mid-write can't leave a half-overwritten env file.
    /// Empty-string values are treated as "skip" -- the existing line
    /// stays untouched and no new line is appended. This lets the V3
    /// discovery flow leave MAGPILOT_HUB_BEARER alone when the
    /// installer didn't supply one yet.
    /// </summary>
    public static void UpsertEnv(string path, IReadOnlyDictionary<string, string> values)
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
            if (values.TryGetValue(key, out var newVal) && !string.IsNullOrEmpty(newVal))
            {
                existing[i] = $"{key}={newVal}";
                seenKeys.Add(key);
            }
        }

        var toAppend = values
            .Where(kvp => !seenKeys.Contains(kvp.Key) && !string.IsNullOrEmpty(kvp.Value))
            .ToList();
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
    /// <c>schtasks.exe</c> so the agent re-reads the env file.
    /// Best-effort; a missing task or schtasks failure logs a one-line
    /// hint and returns false.
    /// </summary>
    public static async Task TryRestartScheduledTaskAsync()
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
