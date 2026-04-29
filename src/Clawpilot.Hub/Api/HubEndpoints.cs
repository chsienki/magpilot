using Clawpilot.Hub.Agents;
using Clawpilot.Hub.Discovery;
using Clawpilot.Shared.Models;

namespace Clawpilot.Hub.Api;

public static class HubEndpoints
{
    public static void MapHubApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", (HttpContext ctx) =>
            Results.Ok(new { identity = ctx.User.Identity?.Name }));

        api.MapGet("/agents", (AgentRegistry reg) => reg.List());

        api.MapPost("/agents", (AgentRegistrationRequest req, AgentRegistry reg) =>
        {
            reg.Upsert(req.Name, req.Url, req.Token, online: true);
            return Results.NoContent();
        });

        api.MapDelete("/agents/{name}", (string name, AgentRegistry reg) =>
            reg.Remove(name) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/agents/discover", async (DiscoveryProber prober, IConfiguration cfg, CancellationToken ct) =>
        {
            var token = cfg["Hub:DefaultAgentToken"] ?? Environment.GetEnvironmentVariable("CLAWPILOT_AGENT_TOKEN");
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
