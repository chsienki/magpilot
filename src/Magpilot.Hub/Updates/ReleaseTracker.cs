using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Magpilot.Shared;

namespace Magpilot.Hub.Updates;

/// <summary>
/// Background service that polls the GitHub Releases API for the
/// configured magpilot release repo, parses the latest release tag and
/// (if shipped) the <c>version.json</c> asset for the supported protocol
/// range, and writes the result into <see cref="ReleaseCache"/>.
///
/// <para>
/// Runs once on startup, then every hour. Failures are logged at Warning
/// (visible in <c>/admin/logs</c>) but never stop the service. The hub
/// keeps serving the last successful answer until the next poll succeeds.
/// </para>
///
/// <para>Configuration (env vars):</para>
/// <list type="bullet">
/// <item><c>MAGPILOT_RELEASE_REPO</c> -- defaults to <c>chsienki/magpilot</c>.</item>
/// <item><c>MAGPILOT_GITHUB_TOKEN</c> -- optional. Bumps the unauthenticated
///       60 req/h GitHub rate limit to 5,000 req/h. We poll once an hour so
///       we don't normally need it.</item>
/// </list>
/// </summary>
public sealed class ReleaseTracker(
    IHttpClientFactory httpFactory,
    ReleaseCache cache,
    ILogger<ReleaseTracker> log,
    IConfiguration cfg) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly string _repo = cfg["Updates:ReleaseRepo"]
        ?? Environment.GetEnvironmentVariable("MAGPILOT_RELEASE_REPO")
        ?? "chsienki/magpilot";

    private readonly string? _token = cfg["Updates:GitHubToken"]
        ?? Environment.GetEnvironmentVariable("MAGPILOT_GITHUB_TOKEN");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("ReleaseTracker started; polling {Repo} every {Interval}", _repo, PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "ReleaseTracker poll failed"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient("releases");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{_repo}/releases/latest");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("magpilot-hub", Versioning.AssemblyVersion));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrEmpty(_token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // 404 from GH releases/latest means EITHER (a) the repo has
            // zero published non-prerelease releases, OR (b) the repo is
            // private and we're polling anonymously (no
            // MAGPILOT_GITHUB_TOKEN set). Both are misconfigurations
            // worth surfacing -- they silently break the autoupdate path
            // for all clients. Log at Warning so /admin/logs shows it.
            log.LogWarning(
                "GitHub releases/latest 404 for {Repo}. Either no published " +
                "release exists yet, or the repo is private and " +
                "MAGPILOT_GITHUB_TOKEN is not set on the hub. Autoupdate banners " +
                "will not fire until this is resolved.", _repo);
            return;
        }
        resp.EnsureSuccessStatusCode();

        var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct);
        if (release?.TagName is null)
        {
            log.LogWarning("Latest release from {Repo} had no tag_name", _repo);
            return;
        }

        var version = release.TagName.TrimStart('v');

        // Try to find a version.json asset to get the protocol range.
        // First release may not have one yet; fall back to baseline 1.
        int min = 1, max = 1;
        var versionAsset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, "version.json", StringComparison.OrdinalIgnoreCase));
        if (versionAsset?.BrowserDownloadUrl is not null)
        {
            try
            {
                using var assetReq = new HttpRequestMessage(HttpMethod.Get, versionAsset.BrowserDownloadUrl);
                assetReq.Headers.UserAgent.Add(new ProductInfoHeaderValue("magpilot-hub", Versioning.AssemblyVersion));
                using var assetResp = await http.SendAsync(assetReq, ct);
                assetResp.EnsureSuccessStatusCode();
                var meta = await assetResp.Content.ReadFromJsonAsync<VersionJson>(cancellationToken: ct);
                if (meta is not null)
                {
                    min = meta.MinProtocol > 0 ? meta.MinProtocol : 1;
                    max = meta.MaxProtocol > 0 ? meta.MaxProtocol : min;
                }
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Failed to fetch version.json asset for {Repo}; using defaults", _repo);
            }
        }

        cache.Set(new LatestVersionInfo(version, min, max, UpdateAvailable: false));
        log.LogInformation("Cached latest release {Version} (protocol [{Min},{Max}])", version, min, max);
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl);

    private sealed record VersionJson(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("minProtocol")] int MinProtocol,
        [property: JsonPropertyName("maxProtocol")] int MaxProtocol);
}
