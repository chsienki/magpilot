using Magpilot.Hub.Auth;
using Magpilot.Hub.Logging;

namespace Magpilot.Hub.Api;

public static class LogEndpoints
{
    public static void MapLogApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/log").RequireAuthorization();

        // Ingest stays open to any authenticated producer: the SPA posts
        // its own JS errors + ILogger output (cookie auth) and agents
        // post batches (infra bearer). Only the QUERY/viewer side below
        // is admin-gated -- central logs aggregate every user's session
        // activity, so a regular multi-user tenant shouldn't read them.
        api.MapPost("/", (LogEventDto evt, LogStore store) =>
        {
            store.Append(new[] { evt });
            return Results.NoContent();
        });

        api.MapPost("/batch", (LogEventBatch batch, LogStore store) =>
        {
            if (batch.Events is null || batch.Events.Count == 0) return Results.NoContent();
            // Soft cap: 500 events per batch keeps a single POST cheap and
            // limits damage from a chatty / runaway client.
            var cap = batch.Events.Count <= 500 ? batch.Events : batch.Events.Take(500).ToList();
            store.Append(cap);
            return Results.NoContent();
        });

        api.MapGet("/", (HttpContext ctx, LogStore store, HubAuthOptions opts) =>
        {
            if (!AgentVisibility.IsAdmin(ctx.User, opts))
                return Results.Json(new { error = "admin only" }, statusCode: StatusCodes.Status403Forbidden);
            var q = ctx.Request.Query;
            var query = new LogQuery(
                Source:    q["source"],
                Level:     q["level"],
                SessionId: q["sessionId"],
                Search:    q["search"],
                Since:     long.TryParse(q["since"], out var ms)
                              ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                              : null,
                Limit:     int.TryParse(q["limit"], out var n) ? n : null);
            return Results.Ok(store.Query(query));
        });

        api.MapGet("/sources", (HttpContext ctx, LogStore store, HubAuthOptions opts) =>
            AgentVisibility.IsAdmin(ctx.User, opts)
                ? Results.Ok(store.KnownSources())
                : Results.Json(new { error = "admin only" }, statusCode: StatusCodes.Status403Forbidden));
    }
}
