using System.Net.Http.Json;
using System.Text.Json;
using Magpilot.Hub.Agents;
using Magpilot.Shared;
using Magpilot.Shared.Models;

namespace Magpilot.Hub.Updates;

/// <summary>
/// Aggregates agent version state and coordinates an immediate release refresh
/// across the hub and its visible agents.
/// </summary>
public sealed class AgentUpdateCoordinator(
    AgentHttpClient agentHttp,
    AgentRegistry registry,
    ReleaseTracker releases,
    ILogger<AgentUpdateCoordinator> log)
{
    public Task<IReadOnlyList<AgentVersionReport>> GetStatusesAsync(
        IReadOnlyList<AgentInfo> agents,
        CancellationToken ct = default) =>
        QueryAgentsAsync(agents, refresh: false, ct);

    public async Task<AgentUpdateCheckResult> CheckForUpdatesAsync(
        IReadOnlyList<AgentInfo> agents,
        CancellationToken ct = default)
    {
        var latest = await releases.RefreshAsync(ct);
        var reports = await QueryAgentsAsync(agents, refresh: true, ct);
        return new AgentUpdateCheckResult(
            latest.LatestVersion,
            DateTimeOffset.UtcNow,
            reports);
    }

    private async Task<IReadOnlyList<AgentVersionReport>> QueryAgentsAsync(
        IReadOnlyList<AgentInfo> agents,
        bool refresh,
        CancellationToken ct)
    {
        var tasks = agents
            .Select(agent => QueryAgentAsync(agent, refresh, ct))
            .ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<AgentVersionReport> QueryAgentAsync(
        AgentInfo agent,
        bool refresh,
        CancellationToken ct)
    {
        if (agent.RevokedAt is not null)
            return new AgentVersionReport(agent.Name, Status: null, Error: "revoked");
        if (string.IsNullOrWhiteSpace(agent.Url))
            return new AgentVersionReport(agent.Name, Status: null, Error: "awaiting discovery");

        try
        {
            using var client = agentHttp.ClientFor(agent.Name);
            AgentVersionStatus? status;
            string? warning = null;
            if (refresh)
            {
                using var response = await client.PostAsync("api/version/refresh", content: null, ct);
                if (response.StatusCode is System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    status = await GetStatusWithLegacyFallbackAsync(client, ct);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    warning = $"refresh failed ({(int)response.StatusCode} {response.ReasonPhrase})";
                    status = await GetStatusWithLegacyFallbackAsync(client, ct);
                }
                else
                {
                    status = await response.Content.ReadFromJsonAsync<AgentVersionStatus>(cancellationToken: ct);
                }
            }
            else
            {
                status = await GetStatusWithLegacyFallbackAsync(client, ct);
            }

            if (status is null)
                return new AgentVersionReport(agent.Name, Status: null, Error: "empty version response");

            registry.MarkOnline(agent.Name);
            return new AgentVersionReport(agent.Name, status, warning);
        }
        catch (AgentVersionResponseException ex)
        {
            registry.MarkOnline(agent.Name);
            return new AgentVersionReport(agent.Name, Status: null, Error: ex.Message);
        }
        catch (JsonException ex)
        {
            registry.MarkOnline(agent.Name);
            log.LogWarning(ex, "Agent returned invalid version JSON for {Agent}", agent.Name);
            return new AgentVersionReport(agent.Name, Status: null, Error: "invalid version response");
        }
        catch (Exception ex) when (
            (ex is HttpRequestException or TaskCanceledException) &&
            !ct.IsCancellationRequested)
        {
            registry.MarkOffline(agent.Name);
            log.LogWarning(ex, "Agent version {Action} failed for {Agent}",
                refresh ? "refresh" : "query", agent.Name);
            return new AgentVersionReport(agent.Name, Status: null, Error: "agent unreachable");
        }
    }

    private static async Task<AgentVersionStatus?> GetStatusWithLegacyFallbackAsync(
        HttpClient client,
        CancellationToken ct)
    {
        using var response = await client.GetAsync("api/version/status", ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            EnsureSuccessful(response, "version status");
            return await response.Content.ReadFromJsonAsync<AgentVersionStatus>(cancellationToken: ct);
        }

        var current = await GetJsonAsync<VersionInfo>(client, "api/version", "running version", ct);
        if (current is null) return null;

        var latest = await GetJsonAsync<LatestVersionInfo>(
            client,
            $"api/version/latest?from={Uri.EscapeDataString(current.Version)}",
            "latest version",
            ct);
        if (latest is null) return null;

        return new AgentVersionStatus(
            current.Version,
            current.ProtocolVersion,
            latest.LatestVersion,
            latest.MinProtocol,
            latest.MaxProtocol,
            latest.UpdateAvailable,
            LastCheckedAt: null,
            RefreshSupported: false);
    }

    private static async Task<T?> GetJsonAsync<T>(
        HttpClient client,
        string path,
        string operation,
        CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        EnsureSuccessful(response, operation);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private static void EnsureSuccessful(
        HttpResponseMessage response,
        string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentVersionResponseException(
                $"{operation} failed ({(int)response.StatusCode} {response.ReasonPhrase})");
        }
    }

    private sealed class AgentVersionResponseException(string message)
        : Exception(message);
}
