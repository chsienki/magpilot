using System.Text.Json;
using Magpilot.Agent.Acp;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Api;

public static class AgentEndpoints
{
    public static void MapAgentApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api").RequireAuthorization();

        api.MapGet("/info", (FlavorCapabilities flavors) => new
        {
            name = Environment.MachineName,
            os = Environment.OSVersion.VersionString,
            cwd = Environment.CurrentDirectory,
            flavors = flavors.Available,
        });

        api.MapGet("/sessions", (SessionRegistry reg) => reg.List()
            .OrderByDescending(s => s.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(s => s.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList());

        api.MapGet("/sessions/{id}", (string id, SessionRegistry reg) =>
        {
            var info = reg.Get(id);
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        // Read the session's events.jsonl directly and project it into a
        // flat role/text/toolCallId list. This is the SPA's escape hatch
        // for hydrating an Owned session that another client (e.g. the WA
        // sidecar) loaded into ACP first -- ACP refuses to load again, so
        // we bypass it entirely.
        api.MapGet("/sessions/{id}/history", (string id, HistoryReader reader) =>
            Results.Ok(reader.Read(id)));

        api.MapPost("/sessions", async (NewSessionRequest req, SessionRegistry reg, AcpSessionManager acp, CancellationToken ct) =>
        {
            var info = await reg.CreateAsync(req.Cwd, req.UseAgency, ct);

            // If the caller provided an initial prompt, fire-and-forget it so the
            // response returns immediately. The caller can subscribe to the SSE
            // stream to watch it run, or hand the user off to the SPA.
            if (!string.IsNullOrEmpty(req.InitialPrompt))
            {
                _ = Task.Run(() => acp.PromptAsync(info.Id, req.InitialPrompt, CancellationToken.None));
            }

            return Results.Ok(info);
        });

        // Synchronous "ask and answer" convenience endpoint. Creates an ephemeral
        // session, sends the prompt, accumulates assistant deltas until the turn
        // completes, and returns the result as a single JSON response. External
        // clients can use this without having to consume the SSE wire format.
        api.MapPost("/quick-prompt", async (
            QuickPromptRequest req, SessionRegistry reg, AcpSessionManager acp,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var log = loggerFactory.CreateLogger("QuickPrompt");
            var timeoutSec = req.TimeoutSeconds ?? 60;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            string sid;
            bool createdEphemeral;
            if (!string.IsNullOrWhiteSpace(req.SessionId))
            {
                // Caller pinned a long-lived session; adopt-on-demand if dormant
                // and route the prompt at it. Never detach in this branch.
                try { await reg.AdoptAsync(req.SessionId, force: false, cts.Token); }
                catch (Exception ex) { log.LogDebug(ex, "QuickPrompt: adopt-on-pin returned {Msg}", ex.Message); }
                sid = req.SessionId;
                createdEphemeral = false;
                log.LogInformation("QuickPrompt: using pinned session {Sid}", sid);

                // Echo the prompt as a UserDelta into the broadcast channel so
                // other connected subscribers (the SPA) render "the user said
                // X" before the assistant deltas arrive. ACP doesn't emit
                // user_message_chunk for live prompts (only during
                // session/load history replay), so without this the SPA would
                // see Magnus's reply but no question.
                acp.PublishToSubscribers(sid, new UserDelta(req.Prompt));
            }
            else
            {
                var info = await reg.CreateAsync(req.Cwd, useAgency: false, cts.Token);
                sid = info.Id;
                createdEphemeral = true;
                log.LogInformation("QuickPrompt: created ephemeral session {Sid}", sid);
            }

            // Subscribe BEFORE sending the prompt so we don't race the first delta.
            var reader = acp.Subscribe(sid);
            var responseText = new System.Text.StringBuilder();
            var stopReason = "unknown";
            string? errorMessage = null;
            try
            {
                // Fire the prompt; PromptAsync resolves when the turn ends.
                _ = Task.Run(() => acp.PromptAsync(sid, req.Prompt, cts.Token), cts.Token);

                // Drain events until TurnComplete or ErrorEvent.
                await foreach (var evt in reader.ReadAllAsync(cts.Token))
                {
                    if (evt is AssistantDelta ad) responseText.Append(ad.Text);
                    else if (evt is TurnComplete tc) { stopReason = tc.StopReason; break; }
                    else if (evt is ErrorEvent ee) { errorMessage = ee.Message; break; }
                }
            }
            catch (OperationCanceledException)
            {
                errorMessage ??= $"timed out after {timeoutSec}s";
            }
            finally
            {
                acp.Unsubscribe(sid, reader);
                // Only detach when we created an ephemeral session AND the caller
                // didn't ask us to keep it. Pinned sessions (SessionId set) are
                // owned by the caller; we never tear them down.
                if (createdEphemeral && !req.KeepSession)
                {
                    try { await reg.DetachAsync(sid, CancellationToken.None); }
                    catch (Exception ex) { log.LogWarning(ex, "QuickPrompt: detach failed for {Sid}", sid); }
                }
            }

            if (errorMessage is not null)
            {
                return Results.Json(
                    new { error = errorMessage, sessionId = sid, partial_text = responseText.ToString() },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(new QuickPromptResponse(responseText.ToString(), stopReason, sid));
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

        api.MapPost("/sessions/{id}/messages", (string id, PromptRequest req, AcpSessionManager acp) =>
        {
            // Fire-and-forget: session/prompt returns when the turn completes
            // (could be 60s+). The endpoint returns 202 immediately and the
            // SPA learns the turn ended via the SSE TurnComplete event.
            _ = Task.Run(() => acp.PromptAsync(id, req.Text, CancellationToken.None));
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
