using System.Net.Http.Json;
using Magpilot.Hub.Agents;
using Magpilot.Hub.Discovery;
using Magpilot.Shared.Models;

namespace Magpilot.Hub.Api;

public static class HubEndpoints
{
    public static void MapHubApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", (HttpContext ctx) =>
            Results.Ok(new { identity = ctx.User.Identity?.Name }));

        api.MapGet("/agents", (AgentRegistry reg) => reg.List());

        api.MapPost("/agents", async (AgentRegistrationRequest req, AgentRegistry reg, AgentHttpClient http, ILoggerFactory lf) =>
        {
            // Initial register so GetToken works for the probe below.
            reg.Upsert(req.Name, req.Url, req.Token, online: true);

            // Best-effort: ask the agent for its capabilities so the SPA can
            // surface flavor-gated UI. We don't fail registration if this
            // doesn't work -- the agent is still usable, just without flavor
            // metadata until the next discovery sweep populates it.
            var log = lf.CreateLogger("AgentRegister");
            try
            {
                var info = await http.ClientFor(req.Name).GetFromJsonAsync<AgentInfoReply>("api/info");
                if (info?.Flavors is { Count: > 0 } flavors)
                {
                    reg.Upsert(req.Name, req.Url, req.Token, online: true, flavors: flavors);
                    log.LogInformation("Registered {Name} with flavors: {Flavors}",
                        req.Name, string.Join(", ", flavors));
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not fetch /api/info from {Name} during registration", req.Name);
            }

            return Results.NoContent();
        });

        api.MapDelete("/agents/{name}", (string name, AgentRegistry reg) =>
            reg.Remove(name) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/agents/discover", async (DiscoveryProber prober, IConfiguration cfg, CancellationToken ct) =>
        {
            var token = cfg["Hub:DefaultAgentToken"] ?? Environment.GetEnvironmentVariable("MAGPILOT_AGENT_TOKEN");
            await prober.ProbeOnceAsync(token, ct);
            return Results.NoContent();
        });

        // ---- Per-agent proxy ---------------------------------------------------
        api.MapGet("/agents/{name}/sessions",
            (string name, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).GetAsync("api/sessions", ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions",
            (string name, NewSessionRequest req, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync("api/sessions", req, ct);
                    return await Forward(resp);
                }));

        api.MapGet("/agents/{name}/sessions/{id}",
            (string name, string id, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).GetAsync($"api/sessions/{id}", ct);
                    return await Forward(resp);
                }));

        api.MapGet("/agents/{name}/sessions/{id}/history",
            (string name, string id, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).GetAsync($"api/sessions/{id}/history", ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/adopt",
            (string name, string id, AdoptRequest req, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/adopt", req, ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/detach",
            (string name, string id, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsync($"api/sessions/{id}/detach", null, ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/messages",
            (string name, string id, PromptRequest req, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/messages", req, ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/interrupt",
            (string name, string id, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsync($"api/sessions/{id}/interrupt", null, ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/approvals/{approvalId}",
            (string name, string id, string approvalId, ApprovalResponse body, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/approvals/{approvalId}", body, ct);
                    return await Forward(resp);
                }));

        // Synchronous "ask and answer" endpoint for external clients. The agent
        // creates an ephemeral session, sends the prompt, accumulates assistant
        // deltas until TurnComplete, and returns the result as a single JSON
        // response. Uses the streaming HttpClient because a turn can take 60s+.
        api.MapPost("/agents/{name}/quick-prompt",
            (string name, QuickPromptRequest req, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var client = http.ClientFor(name, streaming: true);
                    var resp = await client.PostAsJsonAsync("api/quick-prompt", req, ct);
                    return await Forward(resp);
                }));

        // ---- SSE proxy ---------------------------------------------------------
        api.MapGet("/agents/{name}/sessions/{id}/stream",
            async (string name, string id, AgentHttpClient http, AgentRegistry reg, HttpContext ctx, CancellationToken ct) =>
            {
                // SSE uses the no-timeout client; the connection lifetime is controlled by the SPA.
                using var client = http.ClientFor(name, streaming: true);
                var qs = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "";
                using var req = new HttpRequestMessage(HttpMethod.Get, $"api/sessions/{id}/stream{qs}");
                HttpResponseMessage resp;
                try
                {
                    resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    reg.MarkOnline(name);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    reg.MarkOffline(name);
                    ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                    return;
                }
                using (resp)
                {
                    ctx.Response.StatusCode = (int)resp.StatusCode;
                    ctx.Response.Headers.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";
                    await ctx.Response.Body.FlushAsync(ct);
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[8192];
                    int n;
                    while ((n = await stream.ReadAsync(buf, ct)) > 0)
                    {
                        await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
            });

        // ---- Push subscription stub -------------------------------------------
        api.MapPost("/devices", (PushSubscriptionDto sub) =>
        {
            // TODO: persist to subscriptions table; FCM/Web-Push fan-out is push-stub todo.
            return Results.Accepted();
        });
    }

    private static async Task<IResult> Forward(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        return Results.Content(body, resp.Content.Headers.ContentType?.MediaType ?? "application/json", statusCode: (int)resp.StatusCode);
    }

    /// <summary>
    /// Wraps a per-agent proxy call so that transport failures (timeout, refused,
    /// reset) flip the agent to offline and surface 502 to the SPA instead of
    /// hanging the request. Also rewrites upstream 401 to 502 so the SPA's
    /// auth handler doesn't mistake an agent-token mismatch for a hub login
    /// failure (which would loop the user through OAuth indefinitely).
    /// </summary>
    private static async Task<IResult> Proxy(string name, AgentRegistry reg, Func<Task<IResult>> call)
    {
        try
        {
            var result = await call();
            reg.MarkOnline(name);
            // Only the hub itself may issue 401 to the SPA; an upstream 401
            // (agent rejecting our bearer) would otherwise trigger a re-auth loop.
            if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized })
            {
                return Results.Json(
                    new { error = "agent rejected hub bearer (token mismatch)", agent = name },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            return result;
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = $"Unknown agent {name}" });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            reg.MarkOffline(name);
            return Results.Json(new { error = "agent unreachable", agent = name, detail = ex.Message },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}

public sealed record PushSubscriptionDto(string Kind, string Endpoint, string? KeysJson, string? UserAgent);

public sealed record AgentRegistrationRequest(string Name, string Url, string? Token);

/// <summary>Minimal projection of the agent's <c>/api/info</c> response we need.</summary>
internal sealed record AgentInfoReply(string? Name, string? Os, IReadOnlyList<string>? Flavors);
