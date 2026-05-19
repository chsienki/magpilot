using System.Reflection;

namespace Magpilot.Host;

/// <summary>
/// Locates and reads the installer-written <c>magpilot.env</c> file so the
/// launcher can pick up <c>MAGPILOT_AGENT_TOKEN</c> (and friends) without
/// the user having to set them in their shell.
///
/// <para>
/// Resolution: the launcher exe lives at
/// <c>&lt;install-dir&gt;\bin\magpilot.exe</c>; the env file lives at
/// <c>&lt;install-dir&gt;\config\magpilot.env</c>. We walk up one level from
/// the exe's directory and look for <c>config\magpilot.env</c>.
/// </para>
///
/// <para>
/// Process env vars always win over the file (so devs can override per
/// shell without editing the installed file). File misses or read errors
/// are tolerated silently -- the caller falls back to whatever default
/// it would have used absent the file.
/// </para>
/// </summary>
internal static class InstallConfig
{
    /// <summary>
    /// Returns the path to <c>magpilot.env</c> relative to the running
    /// launcher, or null if it can't be located / doesn't exist.
    /// </summary>
    public static string? FindEnvFile()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return null;
        var binDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(binDir)) return null;
        var installDir = Path.GetDirectoryName(binDir);
        if (string.IsNullOrEmpty(installDir)) return null;
        var envFile = Path.Combine(installDir, "config", "magpilot.env");
        return File.Exists(envFile) ? envFile : null;
    }

    /// <summary>
    /// Looks up <paramref name="key"/> in the on-disk env file. Returns null
    /// if the file is missing, unreadable, or the key isn't present.
    /// </summary>
    public static string? ReadValue(string key)
    {
        var envFile = FindEnvFile();
        if (envFile is null) return null;
        try
        {
            foreach (var raw in File.ReadAllLines(envFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line[..eq], key, StringComparison.Ordinal))
                    return line[(eq + 1)..];
            }
        }
        catch
        {
            // Tolerate read errors silently -- caller will fall back.
        }
        return null;
    }

    /// <summary>
    /// Env-var-first, then installer config file. Returns null if neither
    /// has the key set.
    /// </summary>
    public static string? ResolveValue(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } envValue
            ? envValue
            : ReadValue(key);
}
