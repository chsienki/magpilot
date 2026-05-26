using System.Diagnostics;

namespace Magpilot.Host;

/// <summary>
/// Ensures the local magpilot-agent is reachable, kicking it via the
/// installed Windows scheduled task (or, as a fallback, direct exec) if
/// not. Lets users invoke `magpilot` without having to remember to
/// `Start-ScheduledTask -TaskName MagpilotAgent` first.
///
/// <para>
/// Probe: <c>GET /healthz</c> with a tight timeout. If it succeeds, no-op.
/// If it fails, try to start; wait up to <see cref="StartupTimeout"/>
/// for the agent to answer. Returns true iff the agent is reachable by
/// the time we exit.
/// </para>
///
/// <para>
/// Start strategy (Windows; non-Windows is a silent no-op for now):
/// </para>
/// <list type="number">
/// <item><see cref="TryStartScheduledTask"/>: `schtasks /run /tn MagpilotAgent`.
///   This is the preferred path because it runs the agent under the
///   correctly-identified user with the right env loaded via start.ps1.</item>
/// <item><see cref="TryStartDirect"/>: spawn <c>{install}/agent/Magpilot.Agent.exe</c>
///   as a detached process, populating env vars from <see cref="InstallConfig"/>.
///   Fallback for installs that have the agent component but no scheduled
///   task (e.g. user de-selected the schedtask task during install).</item>
/// </list>
///
/// <para>
/// We never throw out of here. Callers should treat a <c>false</c> return as
/// "agent isn't available; proceed in passthrough mode".
/// </para>
/// </summary>
internal static class AgentLauncher
{
    private static readonly TimeSpan QuickProbeTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StartupTimeout    = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbePollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// If the agent is already up, returns true immediately. Otherwise
    /// tries to start it and waits up to <see cref="StartupTimeout"/> for
    /// it to answer. Prints a one-line stderr status when starting so the
    /// user knows what's happening.
    /// </summary>
    public static async Task<bool> EnsureRunningAsync()
    {
        var baseUrl = (InstallConfig.ResolveValue("MAGPILOT_AGENT_URL")
            ?? "http://127.0.0.1:5099").TrimEnd('/');

        if (await IsRunningAsync(baseUrl, QuickProbeTimeout)) return true;

        if (!OperatingSystem.IsWindows())
        {
            // Linux/macOS auto-start is out of scope; user is expected to
            // have systemd / launchd / similar managing the agent.
            return false;
        }

        var howStarted = TryStart();
        if (howStarted is null) return false;

        Console.Error.WriteLine($"magpilot: agent not running; started via {howStarted}, waiting up to {(int)StartupTimeout.TotalSeconds}s...");

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(ProbePollInterval);
            if (await IsRunningAsync(baseUrl, QuickProbeTimeout))
            {
                Console.Error.WriteLine("magpilot: agent ready.");
                return true;
            }
        }

        Console.Error.WriteLine($"magpilot: agent did not become reachable within {(int)StartupTimeout.TotalSeconds}s.");
        return false;
    }

    private static async Task<bool> IsRunningAsync(string baseUrl, TimeSpan timeout)
    {
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            using var resp = await http.GetAsync($"{baseUrl}/healthz");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryStart()
    {
        if (TryStartScheduledTask("MagpilotAgent")) return "scheduled task";
        var agentExe = FindInstalledAgentExe();
        if (agentExe is not null && TryStartDirect(agentExe)) return "direct exec";
        return null;
    }

    private static bool TryStartScheduledTask(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/run /tn \"{taskName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(3000)) return false;
            // schtasks /run returns 0 on success, 267011 ("ERROR: The task
            // is already running") if it's mid-startup or alive. Either
            // way, we want to fall through to the wait-for-ready loop --
            // the task object exists so we treat that as a successful kick.
            // Only a "task does not exist" failure (1) means we should
            // fall back to direct exec.
            return p.ExitCode != 1;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindInstalledAgentExe()
    {
        var launcherDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(launcherDir)) return null;
        // {install}/bin/magpilot.exe -> {install}/agent/Magpilot.Agent.exe
        var installDir = Path.GetDirectoryName(launcherDir);
        if (string.IsNullOrEmpty(installDir)) return null;
        var agentExe = Path.Combine(installDir, "agent", "Magpilot.Agent.exe");
        return File.Exists(agentExe) ? agentExe : null;
    }

    private static bool TryStartDirect(string agentExe)
    {
        try
        {
            var workingDir = Path.GetDirectoryName(agentExe);
            var psi = new ProcessStartInfo(agentExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            };

            // Populate env vars from magpilot.env so the agent has the
            // same configuration the scheduled-task launch would give it.
            // The launcher's process env doesn't automatically include
            // these (start.ps1 sets them); we route via InstallConfig to
            // pick them up either way.
            foreach (var key in new[]
            {
                "MAGPILOT_HUB_URL",
                "MAGPILOT_AGENT_TOKEN",
                "MAGPILOT_HUB_BEARER",
                "MAGPILOT_AGENT_PUBLIC_URL",
            })
            {
                var val = InstallConfig.ResolveValue(key);
                if (!string.IsNullOrEmpty(val))
                    psi.Environment[key] = val;
            }
            psi.Environment["ASPNETCORE_URLS"] = "http://0.0.0.0:5099";

            var p = Process.Start(psi);
            return p is not null;
        }
        catch
        {
            return false;
        }
    }
}
