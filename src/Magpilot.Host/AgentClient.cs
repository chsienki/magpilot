using System.Net.Http.Headers;
using System.Net.Http.Json;
using Magpilot.Shared.Models;

namespace Magpilot.Host;

/// <summary>
/// Thin HTTP client over the magpilot-agent's session endpoints, scoped
/// to the operations the magpilot launcher needs:
/// state lookup, release-request broadcast, atomic acquire-for-host,
/// and release. Reads <c>MAGPILOT_AGENT_URL</c> + <c>MAGPILOT_AGENT_TOKEN</c>
/// from env, defaults the URL to <c>http://127.0.0.1:5099</c>.
/// </summary>
public sealed class AgentClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient _streamHttp;

    public AgentClient(string? agentUrl = null, string? agentToken = null)
    {
        agentUrl   ??= InstallConfig.ResolveValue("MAGPILOT_AGENT_URL")   ?? "http://127.0.0.1:5099";
        agentToken ??= InstallConfig.ResolveValue("MAGPILOT_AGENT_TOKEN") ?? "";
        if (string.IsNullOrEmpty(agentToken))
            throw new InvalidOperationException(
                "MAGPILOT_AGENT_TOKEN is not set (checked env + installer's magpilot.env). " +
                "Set the env var, fix the value in {install}\\config\\magpilot.env, " +
                "or pass --magpilot-skip-check to bypass the agent entirely.");

        var baseUri = new Uri(agentUrl.TrimEnd('/') + "/");

        // Short-timeout client for the quick request/response calls (state,
        // release-request, acquire, release). 15s fails fast if the agent is
        // unreachable so the launcher doesn't hang on startup.
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);

        // Separate client for the long-lived SSE subscribe. It MUST NOT carry a
        // wall-clock timeout: the stream is open for the life of the session
        // (idle but for a ~15s heartbeat), and -- critically -- its reconnect
        // path fetches fresh headers right when the agent is restarting, when
        // Kestrel is blocked ~30-45s by AcpStarter. A 15s timeout there throws
        // TaskCanceledException mid-reconnect; infinite timeout lets the header
        // fetch simply wait for Kestrel to come back. Teardown is driven by the
        // CancellationToken, not the clock.
        _streamHttp = new HttpClient { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        _streamHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
    }

    public string BaseUrl => _http.BaseAddress!.ToString().TrimEnd('/');

    /// <summary>Cheap reachability probe; throws on connect failure.</summary>
    public async Task PingAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("api/info", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<SessionStateInfo?> GetStateAsync(string sessionId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"api/sessions/{sessionId}/state", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(HostWebJsonContext.Default.SessionStateInfo, ct);
    }

    public async Task<SessionStateInfo> AcquireForHostAsync(string sessionId, int hostPid, bool force, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/acquire-for-host",
            new AcquireForHostBody(hostPid, force),
            HostWebJsonContext.Default.AcquireForHostBody,
            ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(HostWebJsonContext.Default.SessionStateInfo, ct))!;
    }

    public async Task<SessionStateInfo> ReleaseAsync(string sessionId, int hostPid, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/release",
            new ReleaseFromHostBody(hostPid),
            HostWebJsonContext.Default.ReleaseFromHostBody,
            ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(HostWebJsonContext.Default.SessionStateInfo, ct))!;
    }

    public async Task FireReleaseRequestAsync(string sessionId, string requester, bool force, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/release-request",
            new ReleaseRequestBody(requester, force),
            HostWebJsonContext.Default.ReleaseRequestBody,
            ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Open the session's SSE stream and yield every <see cref="StreamEvent"/>
    /// until the caller cancels or the stream ends. The wrapper uses this to
    /// watch for a <see cref="ReleaseRequested"/> event so it can begin the
    /// graceful shutdown of its child copilot.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> SubscribeAsync(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/sessions/{sessionId}/stream");
        using var resp = await _streamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) yield break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line[5..].Trim();
            if (string.IsNullOrEmpty(json)) continue;
            StreamEvent? evt = null;
            try { evt = System.Text.Json.JsonSerializer.Deserialize(json, HostGeneralJsonContext.Default.StreamEvent); }
            catch { /* unknown event types are tolerated */ }
            if (evt is not null) yield return evt;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _streamHttp.Dispose();
    }
}
