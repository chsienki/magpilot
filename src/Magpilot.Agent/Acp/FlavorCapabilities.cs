using System.Diagnostics;

namespace Magpilot.Agent.Acp;

/// <summary>
/// Probes the host for which optional ACP flavors are actually launchable.
/// Computed once at agent startup; the result is advertised to the hub via
/// the discovery beacon and the <c>/api/info</c> endpoint so the SPA can
/// hide UI options that aren't available on this particular host (e.g.
/// "Use Agency" should not be shown if the <c>agency</c> CLI isn't installed).
/// </summary>
public sealed class FlavorCapabilities
{
    private readonly ILogger<FlavorCapabilities> _log;

    public IReadOnlyList<string> Available { get; }

    public FlavorCapabilities(ILogger<FlavorCapabilities> log)
    {
        _log = log;
        var list = new List<string> { AcpFlavor.Default.Key };
        if (Probe(AcpFlavor.Agency.Exe, "--version"))
        {
            list.Add(AcpFlavor.Agency.Key);
        }
        Available = list;
        _log.LogInformation("Detected ACP flavors on this host: {Flavors}", string.Join(", ", Available));
    }

    public bool IsAvailable(string flavorKey) =>
        Available.Contains(flavorKey, StringComparer.OrdinalIgnoreCase);

    private bool Probe(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Flavor probe failed for {Exe}", exe);
            return false;
        }
    }
}
