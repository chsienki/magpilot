using System.Net.Http.Headers;
using System.Net.Http.Json;
using Magpilot.Shared;

namespace Magpilot.Agent.Update;

/// <summary>
/// Background service that polls the hub's <c>/api/agent-version</c>
/// endpoint every 15 minutes (after a 30s startup delay so the hub has
/// time to come up if both are launched together) and writes the result
/// into <see cref="LatestVersionCache"/>. The launcher reads from the
/// cache on every invocation via the agent's <c>/api/version/latest</c>.
///
/// <para>
/// No-op when <c>MAGPILOT_HUB_URL</c> or <c>MAGPILOT_HUB_BEARER</c> are
/// unset, so dev runs without a hub still work. Failures are logged at
/// Warning (visible in <c>/admin/logs</c>) but never crash the service;
/// the cache keeps its last successful value until the next good poll.
/// </para>
///
/// <para>
/// The <c>?from=</c> query param tells the hub our current version so it
/// can compute <c>UpdateAvailable</c> for us specifically -- we don't
/// have to make that call locally and risk version-comparison drift
/// between agent and hub.
/// </para>
/// </summary>
public sealed class UpdatePoller(
    IHttpClientFactory httpFactory,
    LatestVersionCache cache,
    ILogger<UpdatePoller> log,
    IConfiguration cfg) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly string? _hubUrl = cfg["Hub:Url"]
        ?? Environment.GetEnvironmentVariable("MAGPILOT_HUB_URL");

    private readonly string? _hubBearer = cfg["Hub:Bearer"]
        ?? Environment.GetEnvironmentVariable("MAGPILOT_HUB_BEARER");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_hubUrl) || string.IsNullOrEmpty(_hubBearer))
        {
            log.LogInformation(
                "UpdatePoller disabled: MAGPILOT_HUB_URL/MAGPILOT_HUB_BEARER not set; cache stays at default");
            return;
        }

        log.LogInformation(
            "UpdatePoller started; polling {Hub}/api/agent-version every {Interval}",
            _hubUrl, PollInterval);

        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "UpdatePoller poll failed"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient("hub-update");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_hubUrl!.TrimEnd('/')}/api/agent-version?from={Versioning.AssemblyVersion}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _hubBearer!);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var info = await resp.Content.ReadFromJsonAsync<LatestVersionInfo>(cancellationToken: ct);
        if (info is null) return;

        cache.Set(info);
        log.LogInformation(
            "Hub reports latest {Latest} (protocol [{Min},{Max}], updateAvailable={Up})",
            string.IsNullOrEmpty(info.LatestVersion) ? "<unknown>" : info.LatestVersion,
            info.MinProtocol, info.MaxProtocol, info.UpdateAvailable);
    }
}
