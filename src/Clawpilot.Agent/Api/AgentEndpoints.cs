using System.Text.Json;
using Clawpilot.Agent.Acp;
using Clawpilot.Agent.Sessions;
using Clawpilot.Shared.Models;

namespace Clawpilot.Agent.Api;

public static class AgentEndpoints
{
    public static void MapAgentApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api").RequireAuthorization();

        api.MapGet("/info", () => new
        {
            name = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            cwd = Environment.CurrentDirectory,
        });

        api.MapGet("/sessions", (SessionRegistry reg) => reg.List());

        api.MapGet("/sessions/{id}", (string id, SessionRegistry reg) =>
        {
            var info = reg.Get(id);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        api.MapPost("/sessions", async (NewSessionRequest req, SessionRegistry reg, CancellationToken ct) =>
        {
            var info = await reg.CreateAsync(req.Cwd, ct);
            return Results.Ok(info);
        });

        api.MapPost("/sessions/{id}/adopt", async (string id, AdoptRequest req, SessionRegistry reg, CancellationToken ct) =>
        {
            try
            {
                var info = await reg.AdoptAsync(id, req.Force, ct);
                return Results.Ok(info);
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (FileNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        api.MapPost("/sessions/{id}/detach", async (string id, SessionRegistry reg, CancellationToken ct) =>
        {
            await reg.DetachAsync(id, ct);
            return Results.NoContent();
        });

        api.MapPost("/sessions/{id}/messages", async (string id, PromptRequest req, AcpSessionManager acp, CancellationToken ct) =>
        {
            await acp.PromptAsync(id, req.Text, ct);
            return Results.Accepted();
        });

        api.MapPost("/sessions/{id}/interrupt", async (string id, AcpSessionManager acp, CancellationToken ct) =>
        {
            await acp.CancelAsync(id, ct);
            return Results.NoContent();
        });

        api.MapPost("/sessions/{id}/approvals/{approvalId}", (string id, string approvalId, ApprovalResponse resp, AcpSessionManager acp) =>
        {
            var ok = acp.ResolveApproval(approvalId, resp.OptionId);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        api.MapGet("/sessions/{id}/stream", async (string id, AcpSessionManager acp, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var reader = acp.Subscribe(id);
            try
            {
                using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var heartbeatTask = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (!await heartbeat.WaitForNextTickAsync(ct)) break;
                        try
                        {
                            await ctx.Response.WriteAsync(": heartbeat\n\n", ct);
                            await ctx.Response.Body.FlushAsync(ct);
                        }
                        catch { return; }
                    }
                }, ct);

                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    var json = JsonSerializer.Serialize<StreamEvent>(evt);
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                acp.Unsubscribe(id, reader);
            }
        });
    }
}
