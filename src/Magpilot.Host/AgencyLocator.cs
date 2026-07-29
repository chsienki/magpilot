namespace Magpilot.Host;

/// <summary>
/// Locates Microsoft's <c>agency</c> CLI (the Agent Platform, https://aka.ms/agency)
/// so the launcher can wrap copilot in it under <c>--magpilot-agency</c>.
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item><c>MAGPILOT_AGENCY</c> env var (explicit override).</item>
///   <item>Walk PATH for <c>agency[.exe]</c>.</item>
///   <item>The per-user install location on Windows
///         (<c>%AppData%\agency\CurrentVersion\agency.exe</c>).</item>
/// </list>
/// Throws <see cref="FileNotFoundException"/> if nothing is found.
/// </remarks>
public static class AgencyLocator
{
    public static string Find()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MAGPILOT_AGENCY");
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"MAGPILOT_AGENCY points at non-existent path: {explicitPath}");
            return explicitPath;
        }

        var exeNames = OperatingSystem.IsWindows() ? new[] { "agency.exe", "agency" } : new[] { "agency" };
        var pathSep = OperatingSystem.IsWindows() ? ';' : ':';
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(pathSep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in pathDirs)
        {
            foreach (var exeName in exeNames)
            {
                string full;
                try { full = Path.GetFullPath(Path.Combine(dir, exeName)); }
                catch { continue; }
                if (File.Exists(full)) return full;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var wellKnown = Path.Combine(roaming, "agency", "CurrentVersion", "agency.exe");
            if (File.Exists(wellKnown)) return wellKnown;
        }

        throw new FileNotFoundException(
            "Could not locate the agency CLI. Install it (https://aka.ms/agency) or set MAGPILOT_AGENCY to its absolute path.");
    }
}
