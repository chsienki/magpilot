using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Clawpilot.Shared.Models;
using Clawpilot.UI.Abstractions;

namespace Clawpilot.UI.Services;

public sealed class HubClient
{
    private readonly HttpClient _http;
    private readonly IHubAuthProvider _auth;

    public HubClient(HttpClient http, IHubAuthProvider auth)
    {
        _http = http;
        _auth = auth;
        // Caller is responsible for setting HttpClient.BaseAddress to an absolute URI.
        if (!auth.UseCookieAuth && auth.BearerToken is { } t)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t);
    }

    public Task<List<AgentInfo>?> ListAgentsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<AgentInfo>>("api/agents", ct);

    public Task<List<SessionInfo>?> ListSessionsAsync(string agent, CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<SessionInfo>>($"api/agents/{agent}/sessions", ct);

    public async Task<SessionInfo?> NewSessionAsync(string agent, string? cwd, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/agents/{agent}/sessions", new NewSessionRequest(cwd, null), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionInfo>(cancellationToken: ct);
    }

    public async Task<SessionInfo?> AdoptAsync(string agent, string id, bool force, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/agents/{agent}/sessions/{id}/adopt", new AdoptRequest(force), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionInfo>(cancellationToken: ct);
    }

    public async Task SendPromptAsync(string agent, string id, string text, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"api/agents/{agent}/sessions/{id}/messages", new PromptRequest(text), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ResolveApprovalAsync(string agent, string id, string approvalId, string optionId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"api/agents/{agent}/sessions/{id}/approvals/{approvalId}",
            new ApprovalResponse(optionId), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        string agent, string id,
        bool load = false, bool force = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"api/agents/{agent}/sessions/{id}/stream";
        if (load) url += $"?load=true&force={(force ? "true" : "false")}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
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
            try { evt = JsonSerializer.Deserialize<StreamEvent>(json); }
            catch { }
            if (evt is not null) yield return evt;
        }
    }
}
