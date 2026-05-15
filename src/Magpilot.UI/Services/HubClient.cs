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
        // Retry-on-409: the agent refuses prompts when the session is held
        // by a magpilot-host wrapper. We knock politely (release-request)
        // then poll state until the host releases, then retry. On final
        // timeout we throw HostStillOwnedException so callers can surface
        // a useful error to the user.
        var resp = await _http.PostAsJsonAsync($"api/agents/{agent}/sessions/{id}/messages", new PromptRequest(text), ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            resp.EnsureSuccessStatusCode();
            return;
        }

        // Try to deserialize the conflict body so we know which host PID
        // is holding the session. If the body shape is unexpected, fall
        // back to a generic conflict error.
        HostOwnedResponse? owned = null;
        try { owned = await resp.Content.ReadFromJsonAsync<HostOwnedResponse>(cancellationToken: ct); }
        catch { /* malformed body -- treat as generic conflict */ }
        if (owned is null || !owned.NeedsRelease)
            throw new InvalidOperationException("Agent rejected the prompt: " + (owned?.Error ?? resp.ReasonPhrase ?? "Conflict"));

        // Polite knock + poll. 60s budget matches the agent's typical
        // max-turn time so a wrapper waiting out a slow LLM response can
        // still hand off cleanly.
        await FireReleaseRequestAsync(agent, id, "spa", force: false, ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var state = await GetStateAsync(agent, id, ct);
            if (state is null || state.Owner != SessionOwner.Host)
                break;
        }

        // Retry once -- if it 409s again, the host didn't release.
        var retry = await _http.PostAsJsonAsync($"api/agents/{agent}/sessions/{id}/messages", new PromptRequest(text), ct);
        if (retry.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new HostStillOwnedException(owned.HostPid,
                $"Terminal session held by PID {owned.HostPid} did not release within 60s. Use the 'Take over' button to force, or close the terminal.");
        retry.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Force-acquire a session held by a magpilot-host wrapper, then send
    /// the prompt. Used by the SPA's "Take over from terminal" button --
    /// bypasses the polite release-request and aborts any in-flight turn
    /// the wrapper had running.
    /// </summary>
    public async Task ForceTakeOverAndSendAsync(string agent, string id, string text, CancellationToken ct = default)
    {
        // host_pid 0 because the SPA isn't really a host -- it's just
        // taking over so the agent can drive again. The agent treats the
        // PID as advisory after the swap; HostOwnership is cleared.
        await AcquireForHostAsync(agent, id, hostPid: 0, force: true, ct);
        // Immediately release so the agent re-adopts.
        await ReleaseAsync(agent, id, hostPid: 0, ct);
        // Now the prompt should land cleanly.
        await SendPromptAsync(agent, id, text, ct);
    }

    /// <summary>
    /// Fetch the rich session state (owner, activity, in-flight info,
    /// last-event timestamp) for the take-over UX.
    /// </summary>
    public async Task<SessionStateInfo?> GetStateAsync(string agent, string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"api/agents/{agent}/sessions/{id}/state", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionStateInfo>(cancellationToken: ct);
    }

    /// <summary>
    /// Broadcast a <c>release_requested</c> SSE event so any subscribed
    /// magpilot-host wrapper can begin its graceful shutdown.
    /// </summary>
    public async Task FireReleaseRequestAsync(string agent, string id, string requester, bool force, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"api/agents/{agent}/sessions/{id}/release-request",
            new ReleaseRequestBody(requester, force),
            ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<SessionStateInfo> AcquireForHostAsync(string agent, string id, int hostPid, bool force, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"api/agents/{agent}/sessions/{id}/acquire-for-host",
            new AcquireForHostBody(hostPid, force),
            ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessionStateInfo>(cancellationToken: ct))!;
    }

    public async Task<SessionStateInfo> ReleaseAsync(string agent, string id, int hostPid, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"api/agents/{agent}/sessions/{id}/release",
            new ReleaseFromHostBody(hostPid),
            ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SessionStateInfo>(cancellationToken: ct))!;
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
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (HttpRequestException)
            {
                // Underlying SSE socket was torn down (very common when a
                // mobile browser backgrounds the tab and the OS drops the
                // connection -- WASM resumes and ReadLineAsync surfaces a
                // 'TypeError: network error'). Treat as end-of-stream so the
                // caller can decide whether to reconnect, instead of
                // bubbling up as an unhandled exception.
                yield break;
            }
            catch (IOException)
            {
                // Same intent as above for native runtimes (MAUI host) where
                // the failure mode is an IOException rather than a Browser
                // HttpRequestException.
                yield break;
            }
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

/// <summary>
/// Thrown by <see cref="HubClient.SendPromptAsync"/> when the agent
/// refuses the prompt because a magpilot-host wrapper holds the session
/// AND the wrapper failed to release within our timeout window. Callers
/// (e.g. ChatView) can catch this to surface a meaningful UI hint plus
/// an optional "Take over from terminal" affordance.
/// </summary>
public sealed class HostStillOwnedException(int hostPid, string message) : Exception(message)
{
    public int HostPid { get; } = hostPid;
}
