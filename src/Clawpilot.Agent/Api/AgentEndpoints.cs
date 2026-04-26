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

        api.MapGet("/sessions/{id}/stream", async (string id, AcpSessionManager acp, SessionRegistry reg, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            // Single writer pattern: every producer (acp updates, heartbeat,
            // load lifecycle) writes into this channel; one task drains it
            // to ctx.Response. Avoids interleaved bytes mid-event.
            var outbound = System.Threading.Channels.Channel.CreateUnbounded<StreamEvent>(
                new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

            var reader = acp.Subscribe(id);

            // Commit response headers immediately so the client's fetch promise
            // resolves before the first heartbeat.
            await ctx.Response.Body.FlushAsync(ct);

            // Bridge upstream session-update events into the outbound channel.
            var bridgeTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var evt in reader.ReadAllAsync(ct))
                        outbound.Writer.TryWrite(evt);
                }
                catch (OperationCanceledException) { }
            }, ct);

            // Heartbeat.
            var heartbeatTask = Task.Run(async () =>
            {
                try
                {
                    using var t = new PeriodicTimer(TimeSpan.FromSeconds(15));
                    while (await t.WaitForNextTickAsync(ct))
                        outbound.Writer.TryWrite(new HeartbeatEvent());
                }
                catch (OperationCanceledException) { }
            }, ct);

            // Optional load-on-connect: kick off after subscribe so replay
            // events captured by the subscriber list flow into outbound.
            var loadRequested = ctx.Request.Query.TryGetValue("load", out var lv)
                && bool.TryParse(lv, out var lb) && lb;
            var force = ctx.Request.Query.TryGetValue("force", out var fv)
                && bool.TryParse(fv, out var fb) && fb;
            if (loadRequested)
            {
                _ = Task.Run(async () =>
                {
                    outbound.Writer.TryWrite(new LoadStarted());
                    try
                    {
                        await reg.AdoptAsync(id, force, ct);
                        outbound.Writer.TryWrite(new HistoryDone());
                    }
                    catch (Exception ex)
                    {
                        outbound.Writer.TryWrite(new LoadFailed(ex.Message));
                    }
                }, ct);
            }

            try
            {
                await foreach (var evt in outbound.Reader.ReadAllAsync(ct))
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
