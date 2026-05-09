using Magpilot.Hub.Logging;

namespace Magpilot.Hub.Api;

public static class LogEndpoints
{
    public static void MapLogApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api/log").RequireAuthorization();

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

        api.MapGet("/", (HttpContext ctx, LogStore store) =>
        {
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

        api.MapGet("/sources", (LogStore store) => Results.Ok(store.KnownSources()));
    }
}
