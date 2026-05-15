using System.Net.Http.Json;
using Magpilot.Shared;

namespace Magpilot.Host;

/// <summary>
/// Best-effort upgrade-banner check. Hits the local agent's
/// <c>/api/version/latest</c> endpoint with a tight timeout and prints a
/// one-line stderr banner if the hub-aware agent reports a newer release.
/// All failures are silent: if the agent isn't running, the user is
/// offline, or the endpoint hasn't shipped yet, we just don't print
/// the banner.
/// </summary>
internal static class UpdateBanner
{
    public static async Task MaybePrintAsync()
    {
        try
        {
            var baseUrl = (Environment.GetEnvironmentVariable("MAGPILOT_AGENT_URL")
                ?? "http://127.0.0.1:5099").TrimEnd('/');
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            var info = await http.GetFromJsonAsync<LatestVersionInfo>($"{baseUrl}/api/version/latest");
            if (info is null) return;
            if (info.UpdateAvailable && !string.IsNullOrEmpty(info.LatestVersion))
            {
                Console.Error.WriteLine(
                    $"magpilot: {info.LatestVersion} available (current: {Versioning.AssemblyVersion}). " +
                    "run `magpilot --magpilot-update` to install.");
            }
        }
        catch
        {
            // Silent: this banner is opportunistic, not contractual.
        }
    }
}
