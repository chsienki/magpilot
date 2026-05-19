using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Magpilot.Shared;

namespace Magpilot.Host;

/// <summary>
/// Implements <c>magpilot --magpilot-update</c>: ask the local agent for
/// the latest known release, download the matching installer from GitHub
/// Releases, validate its SHA256 against the published <c>.sha256</c>
/// asset, and launch it silently. Inno Setup's
/// <c>CloseApplications=force</c> handles the launcher-exe-in-use case
/// and our own scheduled-task agent stop/restart.
///
/// <para>
/// Exit codes:
/// </para>
/// <list type="table">
/// <item><term>0</term><description>Already on latest, or installer launched successfully.</description></item>
/// <item><term>5</term><description>Agent unreachable.</description></item>
/// <item><term>6</term><description>Installer download failed.</description></item>
/// <item><term>7</term><description>SHA256 missing, mismatch, or fetch failed.</description></item>
/// <item><term>8</term><description>Installer launch failed.</description></item>
/// </list>
/// </summary>
internal static class UpdateInstaller
{
    private const string ReleaseRepo = "chsienki/magpilot";

    public static async Task<int> RunAsync()
    {
        var baseUrl = (InstallConfig.ResolveValue("MAGPILOT_AGENT_URL")
            ?? "http://127.0.0.1:5099").TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        LatestVersionInfo? info;
        try
        {
            info = await http.GetFromJsonAsync<LatestVersionInfo>(
                $"{baseUrl}/api/version/latest?from={Versioning.AssemblyVersion}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: cannot reach agent at {baseUrl} ({ex.GetType().Name}: {ex.Message})");
            Console.Error.WriteLine("magpilot: is the agent running? (try: magpilot --magpilot-status)");
            return 5;
        }

        if (info is null || string.IsNullOrEmpty(info.LatestVersion))
        {
            Console.WriteLine("magpilot: agent reports no known release; nothing to update to.");
            return 0;
        }

        if (!info.UpdateAvailable)
        {
            Console.WriteLine($"magpilot: already on latest ({info.LatestVersion}).");
            return 0;
        }

        var ver = info.LatestVersion;
        var installerName = $"magpilot-setup-{ver}.exe";
        var installerUrl = $"https://github.com/{ReleaseRepo}/releases/download/v{ver}/{installerName}";
        var sha256Url = installerUrl + ".sha256";

        var tempDir = Path.Combine(Path.GetTempPath(), $"magpilot-update-{ver}");
        Directory.CreateDirectory(tempDir);
        var installerPath = Path.Combine(tempDir, installerName);

        Console.WriteLine($"magpilot: downloading {installerUrl}");
        try
        {
            await DownloadAsync(http, installerUrl, installerPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: download failed ({ex.GetType().Name}: {ex.Message})");
            return 6;
        }

        Console.WriteLine("magpilot: validating SHA256...");
        try
        {
            var shaResponse = (await http.GetStringAsync(sha256Url)).Trim();
            // GitHub-style sha256 file is "<hex>  <filename>"; we just want the hex.
            var expectedSha = shaResponse.Split([' ', '\t'], 2,
                StringSplitOptions.RemoveEmptyEntries)[0];
            var actualSha = ComputeSha256(installerPath);
            if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"magpilot: SHA256 mismatch (expected {expectedSha}, got {actualSha})");
                Console.Error.WriteLine("magpilot: refusing to install tampered installer.");
                return 7;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: SHA256 fetch/check failed ({ex.GetType().Name}: {ex.Message})");
            Console.Error.WriteLine("magpilot: refusing to install unverified installer.");
            return 7;
        }

        Console.WriteLine($"magpilot: launching installer ({installerPath})...");
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true,
        };
        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine("magpilot: failed to launch installer.");
                return 8;
            }
            // Don't wait on the installer here -- on Windows it may need to
            // replace this very executable, and Inno's standard rename-then-
            // replace + CloseApplications=force handle it cleanly only if
            // we exit promptly.
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: failed to launch installer ({ex.GetType().Name}: {ex.Message})");
            return 8;
        }
    }

    private static async Task DownloadAsync(HttpClient http, string url, string destPath)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var dest = File.Create(destPath);
        await resp.Content.CopyToAsync(dest);
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Implements <c>magpilot --magpilot-version</c>: print local + agent-
/// reported version info to stdout. Useful for debugging update checks.
/// </summary>
internal static class VersionPrinter
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"magpilot {Versioning.AssemblyVersion} (protocol {Versioning.ProtocolVersion})");
        try
        {
            var baseUrl = (InstallConfig.ResolveValue("MAGPILOT_AGENT_URL")
                ?? "http://127.0.0.1:5099").TrimEnd('/');
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var info = await http.GetFromJsonAsync<LatestVersionInfo>(
                $"{baseUrl}/api/version/latest?from={Versioning.AssemblyVersion}");
            if (info is null)
            {
                Console.WriteLine("  agent-reported latest: <unknown>");
            }
            else
            {
                var latest = string.IsNullOrEmpty(info.LatestVersion) ? "<unknown>" : info.LatestVersion;
                Console.WriteLine($"  agent-reported latest: {latest} (protocol [{info.MinProtocol},{info.MaxProtocol}])");
                Console.WriteLine($"  update available: {info.UpdateAvailable}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  agent-reported latest: <agent unreachable> ({ex.GetType().Name})");
        }
        return 0;
    }
}
