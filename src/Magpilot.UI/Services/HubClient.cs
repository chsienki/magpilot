using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Magpilot.Shared.Models;
using Magpilot.UI.Abstractions;
using Magpilot.UI.Components;

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

    /// <summary>
    /// Fetch the currently signed-in user's identity from
    /// <c>GET /api/me</c>. Used by Home.razor to strip the owner
    /// prefix from <c>SessionInfo.Repository</c> when it matches the
    /// signed-in user, so the session list isn't dominated by
    /// repeated <c>username/</c> prefixes.
    /// </summary>
    public Task<MeInfo?> GetMeAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<MeInfo>("api/me", ct);

    public sealed record MeInfo(string? Identity);

    /// <summary>
    /// Mint a fresh one-time enrollment voucher on the hub. Each call
    /// produces a different voucher (15min TTL, single-use). Returns
    /// the encoded bundle string ("magpilot2+...") the user pastes
    /// into <c>magpilot --magpilot-pair=&lt;bundle&gt;</c>. Throws
    /// <see cref="EnrollmentNotReadyException"/> with the hub's
    /// explanation when the hub isn't configured to issue vouchers
    /// yet (typically: missing MAGPILOT_HUB_PUBLIC_URL or running
    /// with the dev defaults).
    /// </summary>
    public async Task<string> CreateEnrollmentVoucherAsync(CancellationToken ct = default)
    {
        var resp = await _http.PostAsync("api/admin/enroll/voucher", content: null, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            string? hint = null;
            try
            {
                var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (body.TryGetProperty("error", out var e)) hint = e.GetString();
            }
            catch { /* malformed body -- fall through with the default hint */ }
            throw new EnrollmentNotReadyException(hint ?? "The hub isn't configured to issue enrollment vouchers yet.");
        }
        resp.EnsureSuccessStatusCode();
        var ok = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return ok.GetProperty("encoded").GetString()
            ?? throw new InvalidOperationException("Enrollment voucher response had no 'encoded' field.");
    }

    public Task<List<AgentInfo>?> ListAgentsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<AgentInfo>>("api/agents", ct);

    /// <summary>
    /// Revoke a paired agent. Hub clears the per-agent token + sets
    /// <c>revoked_at</c>. Subsequent calls to that agent return 410
    /// from the hub's Proxy wrapper. Reversible by re-enrolling via
    /// a fresh voucher. Returns the refreshed <see cref="AgentInfo"/>;
    /// 404 surfaces as <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<AgentInfo?> RevokeAgentAsync(string name, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"api/admin/agents/{Uri.EscapeDataString(name)}/revoke", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AgentInfo>(cancellationToken: ct);
    }

    public Task<List<SessionInfo>?> ListSessionsAsync(string agent, CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<SessionInfo>>($"api/agents/{agent}/sessions", ct);

    /// <summary>
    /// Read the persisted message history for a session straight from the
    /// agent's events.jsonl projection. Defaults to the most recent 50
    /// entries; pass <paramref name="tail"/> to override or
    /// <paramref name="before"/> to demand-load older entries.
    /// </summary>
    /// <param name="tail">If set, return the most recent N entries (default 50).</param>
    /// <param name="before">If set, return up to <paramref name="limit"/> entries immediately older than this ordinal cursor. Pass the previous page's <see cref="HistoryPage.OldestCursor"/>.</param>
    /// <param name="limit">Page size when <paramref name="before"/> is set (default 50).</param>
    public Task<HistoryPage?> GetHistoryAsync(
        string agent,
        string sessionId,
        int? tail = null,
        int? before = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>();
        if (before is int b) { qs.Add($"before={b}"); if (limit is int l) qs.Add($"limit={l}"); }
        else if (tail is int t) { qs.Add($"tail={t}"); }
        var url = $"api/agents/{agent}/sessions/{sessionId}/history";
        if (qs.Count > 0) url += "?" + string.Join("&", qs);
        return _http.GetFromJsonAsync<HistoryPage>(url, ct);
    }

    public sealed record HistoryEntry(int Id, string Role, string Text, string? ToolCallId = null, ToolStatus ToolStatus = ToolStatus.Pending);
    public sealed record HistoryPage(IReadOnlyList<HistoryEntry> Entries, int OldestCursor, bool HasMore);

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

    /// <summary>
    /// Detach the agent's ACP copy of the session, dropping it back to
    /// Dormant on disk. The agent's <c>SessionRegistry.DetachAsync</c>
    /// removes the session from its owned map and calls
    /// <c>session/close</c> (which is a no-op in the current copilot
    /// CLI, but that's an upstream limitation -- see the gotcha in the
    /// magpilot copilot-instructions). Returns 204 on success; we
    /// surface errors with the standard EnsureSuccessStatusCode path.
    /// </summary>
    public async Task DetachAsync(string agent, string id, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"api/agents/{agent}/sessions/{id}/detach", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Stop the in-flight turn for the given session by issuing
    /// <c>POST /sessions/{id}/interrupt</c>, which on the agent calls
    /// ACP <c>session/cancel</c>. The model stops generating at the next
    /// chunk boundary; the agent still emits a <c>TurnComplete</c> so
    /// the SPA's <c>_busy</c> flag flips back to idle. Idempotent; safe
    /// to call when no turn is active (the agent returns 204 either way).
    /// 409 means a magpilot launcher holds the session -- not our
    /// problem here, the launcher will respond to its own SIGINT path.
    /// </summary>
    public async Task InterruptAsync(string agent, string id, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"api/agents/{agent}/sessions/{id}/interrupt", content: null, ct);
        // Tolerate 409 (host-owned) and 404 (no in-flight turn) silently;
        // both are "we already aren't running anything from this client".
        if (resp.StatusCode is System.Net.HttpStatusCode.Conflict
            or System.Net.HttpStatusCode.NotFound) return;
        resp.EnsureSuccessStatusCode();
    }

    public async Task SendPromptAsync(string agent, string id, string text, CancellationToken ct = default)
    {
        // Retry-on-409: the agent refuses prompts when the session is held
        // by a magpilot launcher. We knock politely (release-request)
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
    /// Force-acquire a session held by a magpilot launcher, then send
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
    /// magpilot launcher can begin its graceful shutdown.
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

    /// <summary>
    /// Flip the per-session yolo mode bit on the agent. When yolo is on,
    /// the agent auto-approves every <c>session/request_permission</c>
    /// for this session, bypassing the SSE approval round-trip. The
    /// returned <see cref="SessionStateInfo"/> reflects the new state
    /// in <c>Info.Yolo</c>. Throws <see cref="YoloDisabledException"/>
    /// if the agent's host has <c>MAGPILOT_YOLO_DISABLED=true</c> set
    /// (403), so the SPA can surface a meaningful error and stop
    /// offering the toggle on that host.
    /// </summary>
    public async Task<SessionStateInfo> SetYoloAsync(string agent, string id, bool enabled, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"api/agents/{agent}/sessions/{id}/yolo",
            new YoloRequest(enabled),
            ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new YoloDisabledException("Yolo mode is disabled on this host (MAGPILOT_YOLO_DISABLED=true).");
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

        // Open the SSE stream. The initial request can fail in two ways
        // we want the caller to treat as "stream ended, please reconnect"
        // rather than as a hard exception:
        //   - 502/504 from the hub (NPM timeout reaching a slow agent)
        //   - TypeError/HttpRequestException from the browser fetch layer
        //     (DNS hiccup, captive portal, the user toggled WiFi, etc.)
        // Same yield-break treatment as the mid-stream drop case below;
        // Home.razor's pump turns the natural foreach exit into a
        // reconnect with backoff + a visible Reconnecting status pill.
        HttpResponseMessage? resp;
        Stream? body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                resp.Dispose();
                yield break;
            }
            body = await resp.Content.ReadAsStreamAsync(ct);
        }
        catch (HttpRequestException)
        {
            yield break;
        }
        catch (IOException)
        {
            yield break;
        }

        // We can't `yield break` inside a `try` that owns IDisposables in
        // C#, so the resp/body lifetimes are managed via try/finally below
        // around the loop body. The using-pattern above (inside the try)
        // would have disposed everything before the foreach yielded its
        // first item.
        using (resp)
        await using (body)
        {
            using var reader = new StreamReader(body);
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
/// refuses the prompt because a magpilot launcher holds the session
/// AND the wrapper failed to release within our timeout window. Callers
/// (e.g. ChatView) can catch this to surface a meaningful UI hint plus
/// an optional "Take over from terminal" affordance.
/// </summary>
public sealed class HostStillOwnedException(int hostPid, string message) : Exception(message)
{
    public int HostPid { get; } = hostPid;
}

/// <summary>
/// Thrown by <see cref="HubClient.SetYoloAsync"/> when the agent's
/// host has <c>MAGPILOT_YOLO_DISABLED=true</c> set. Surfaces as 403
/// from the agent and lets the SPA disable the toggle + show a tooltip
/// rather than silently failing.
/// </summary>
public sealed class YoloDisabledException(string message) : Exception(message);

/// <summary>
/// Thrown by <see cref="HubClient.GetEnrollmentBundleAsync"/> when the
/// hub returns 503 because it isn't configured to issue bundles yet
/// (typically: running with dev defaults, or
/// <c>MAGPILOT_HUB_PUBLIC_URL</c> is unset). The message is the hub's
/// hint about what to set; surface it directly to the user so the
/// setup gap is one fix away.
/// </summary>
public sealed class EnrollmentNotReadyException(string message) : Exception(message);
