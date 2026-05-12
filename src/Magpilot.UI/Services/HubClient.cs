using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Magpilot.Shared.Models;
using Magpilot.UI.Abstractions;

namespace Magpilot.UI.Services;

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

    /// <summary>
    /// Read the persisted message history for a session straight from the
    /// agent's events.jsonl projection. Used to rehydrate an Owned session
    /// in a fresh browser tab when the in-memory cache is empty (we can't
    /// re-trigger ACP's session/load to replay it).
    /// </summary>
    public Task<List<HistoryEntry>?> GetHistoryAsync(string agent, string sessionId, CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<HistoryEntry>>($"api/agents/{agent}/sessions/{sessionId}/history", ct);

    public sealed record HistoryEntry(string Role, string Text, string? ToolCallId = null);

    public async Task<SessionInfo?> NewSessionAsync(string agent, string? cwd, bool useAgency = false, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"api/agents/{agent}/sessions",
            new NewSessionRequest(cwd, null, null, useAgency),
            ct);
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

    /// <summary>
    /// Distinct list of source names (agent host names + "spa" + "hub") that
    /// have at least one row in the central log -- used to populate the
    /// Source filter dropdown on the /admin/logs viewer.
    /// </summary>
    public Task<List<string>?> ListLogSourcesAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<string>>("api/log/sources", ct);

    /// <summary>
    /// Read recent rows from the central log. All filters are optional; the
    /// hub returns at most <paramref name="limit"/> rows newest-first. Used
    /// by the /admin/logs viewer for both the initial load and each filter
    /// change.
    /// </summary>
    public Task<List<LogEntry>?> ListLogsAsync(
        string? source = null,
        string? level = null,
        string? search = null,
        string? sessionId = null,
        long? sinceUnixMs = null,
        int limit = 500,
        CancellationToken ct = default)
    {
        var qs = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrEmpty(source))    qs.Add($"source={Uri.EscapeDataString(source)}");
        if (!string.IsNullOrEmpty(level))     qs.Add($"level={Uri.EscapeDataString(level)}");
        if (!string.IsNullOrEmpty(search))    qs.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrEmpty(sessionId)) qs.Add($"sessionId={Uri.EscapeDataString(sessionId)}");
        if (sinceUnixMs is { } s)             qs.Add($"since={s}");
        return _http.GetFromJsonAsync<List<LogEntry>>("api/log?" + string.Join("&", qs), ct);
    }

    /// <summary>One row from the central log as exposed by /api/log.</summary>
    public sealed record LogEntry(
        long Id,
        DateTimeOffset Timestamp,
        string Source,
        string Level,
        string? Category,
        string Message,
        string? Stack,
        string? SessionId,
        string? Extra,
        string? UserAgent,
        string? Url);
}
