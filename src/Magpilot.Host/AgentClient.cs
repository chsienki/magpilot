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

    public AgentClient(string? agentUrl = null, string? agentToken = null)
    {
        agentUrl   ??= Environment.GetEnvironmentVariable("MAGPILOT_AGENT_URL")   ?? "http://127.0.0.1:5099";
        agentToken ??= Environment.GetEnvironmentVariable("MAGPILOT_AGENT_TOKEN") ?? "";
        if (string.IsNullOrEmpty(agentToken))
            throw new InvalidOperationException(
                "MAGPILOT_AGENT_TOKEN env var is required (or pass --magpilot-skip-check to bypass the agent entirely).");

        _http = new HttpClient { BaseAddress = new Uri(agentUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken);
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
        return await resp.Content.ReadFromJsonAsync<SessionStateInfo>(cancellationToken: ct);
    }

    public async Task<SessionStateInfo> AcquireForHostAsync(string sessionId, int hostPid, bool force, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/acquire-for-host",
            new AcquireForHostBody(hostPid, force),
            ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessionStateInfo>(cancellationToken: ct))!;
    }

    public async Task<SessionStateInfo> ReleaseAsync(string sessionId, int hostPid, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/release",
            new ReleaseFromHostBody(hostPid),
            ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessionStateInfo>(cancellationToken: ct))!;
    }

    public async Task FireReleaseRequestAsync(string sessionId, string requester, bool force, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"api/sessions/{sessionId}/release-request",
            new ReleaseRequestBody(requester, force),
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
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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
            try { evt = System.Text.Json.JsonSerializer.Deserialize<StreamEvent>(json); }
            catch { /* unknown event types are tolerated */ }
            if (evt is not null) yield return evt;
        }
    }

    public void Dispose() => _http.Dispose();
}
