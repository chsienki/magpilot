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

        api.MapPost("/agents", (AgentInfo info, AgentRegistry reg) =>
        {
            reg.Upsert(info.Name, info.Url, null, info.Online);
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
            async (string name, AgentHttpClient http) =>
                Results.Content(await http.ClientFor(name).GetStringAsync("api/sessions"), "application/json"));

        api.MapPost("/agents/{name}/sessions",
            async (string name, NewSessionRequest req, AgentHttpClient http) =>
            {
                var resp = await http.ClientFor(name).PostAsJsonAsync("api/sessions", req);
                return await Forward(resp);
            });

        api.MapGet("/agents/{name}/sessions/{id}",
            async (string name, string id, AgentHttpClient http) =>
            {
                var resp = await http.ClientFor(name).GetAsync($"api/sessions/{id}");
                return await Forward(resp);
            });

        api.MapPost("/agents/{name}/sessions/{id}/adopt",
            async (string name, string id, AdoptRequest req, AgentHttpClient http) =>
            {
                var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/adopt", req);
                return await Forward(resp);
            });

        api.MapPost("/agents/{name}/sessions/{id}/detach",
            async (string name, string id, AgentHttpClient http) =>
            {
                var resp = await http.ClientFor(name).PostAsync($"api/sessions/{id}/detach", null);
                return await Forward(resp);
            });

        api.MapPost("/agents/{name}/sessions/{id}/messages",
            async (string name, string id, PromptRequest req, AgentHttpClient http) =>
            {
                var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/messages", req);
                return await Forward(resp);
            });

        api.MapPost("/agents/{name}/sessions/{id}/interrupt",
            async (string name, string id, AgentHttpClient http) =>
            {
                var resp = await http.ClientFor(name).PostAsync($"api/sessions/{id}/interrupt", null);
                return await Forward(resp);
            });

        api.MapPost("/agents/{name}/sessions/{id}/approvals/{approvalId}",
            async (string name, string id, string approvalId, ApprovalResponse body, AgentHttpClient http) =>
            {
                var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/approvals/{approvalId}", body);
                return await Forward(resp);
            });

        // ---- SSE proxy ---------------------------------------------------------
        api.MapGet("/agents/{name}/sessions/{id}/stream",
            async (string name, string id, AgentHttpClient http, HttpContext ctx, CancellationToken ct) =>
            {
                using var client = http.ClientFor(name);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"api/sessions/{id}/stream");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.Headers.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[8192];
                int n;
                while ((n = await stream.ReadAsync(buf, ct)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buf.AsMemory(0, n), ct);
                    await ctx.Response.Body.FlushAsync(ct);
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
}

public sealed record PushSubscriptionDto(string Kind, string Endpoint, string? KeysJson, string? UserAgent);
