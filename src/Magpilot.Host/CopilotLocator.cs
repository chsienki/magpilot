namespace Magpilot.Host;

/// <summary>
/// Locates the real copilot binary on this machine -- carefully avoiding
/// the wrapper itself, since aliasing <c>copilot</c> to the wrapper means
/// a naive PATH lookup would just point back at us and recurse.
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item><c>MAGPILOT_REAL_COPILOT</c> env var (explicit override).</item>
///   <item>Walk PATH; pick the first entry whose resolved file is NOT
///         the wrapper's own executable.</item>
///   <item>Well-known install locations (winget on Windows, common
///         package-manager paths on Linux/macOS).</item>
/// </list>
/// Throws <see cref="FileNotFoundException"/> if nothing is found.
/// </remarks>
public static class CopilotLocator
{
    public static string Find()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MAGPILOT_REAL_COPILOT");
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"MAGPILOT_REAL_COPILOT points at non-existent path: {explicitPath}");
            return explicitPath;
        }

        var ourSelf = Path.GetFullPath(Environment.ProcessPath ?? "");
        var ourSelfNoExt = Path.GetFileNameWithoutExtension(ourSelf);
        var exeNames = OperatingSystem.IsWindows() ? new[] { "copilot.exe", "copilot" } : new[] { "copilot" };
        var pathSep  = OperatingSystem.IsWindows() ? ';' : ':';
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(pathSep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in pathDirs)
        {
            foreach (var exeName in exeNames)
            {
                string full;
                try { full = Path.GetFullPath(Path.Combine(dir, exeName)); }
                catch { continue; }
                if (!File.Exists(full)) continue;
                if (string.Equals(full, ourSelf, StringComparison.OrdinalIgnoreCase)) continue;
                // Also skip if the file is named like our wrapper (defensive
                // against same-named symlinks pointing at us).
                if (string.Equals(Path.GetFileNameWithoutExtension(full), ourSelfNoExt, StringComparison.OrdinalIgnoreCase))
                    continue;
                // On Windows, we explicitly want the real copilot.exe, not
                // some shim named "copilot.exe" that itself wraps something.
                // The user has MAGPILOT_REAL_COPILOT for that case if they
                // need to be specific.
                return full;
            }
        }

        // Well-known locations (best-effort).
        var fallbacks = OperatingSystem.IsWindows()
            ? new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Microsoft", "WinGet", "Packages",
                             "GitHub.Copilot_Microsoft.Winget.Source_8wekyb3d8bbwe", "copilot.exe"),
              }
            : new[] {
                "/usr/local/bin/copilot",
                "/opt/homebrew/bin/copilot",
                "/usr/bin/copilot",
              };
        foreach (var f in fallbacks)
            if (File.Exists(f)) return f;

        throw new FileNotFoundException(
            "Could not locate the real copilot binary. Set MAGPILOT_REAL_COPILOT to its absolute path.");
    }
}
