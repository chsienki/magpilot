using System.Net.Http.Json;
using Magpilot.Hub.Agents;
using Magpilot.Hub.Discovery;
using Magpilot.Hub.Updates;
using Magpilot.Shared;
using Magpilot.Shared.Models;

namespace Magpilot.Hub.Api;

public static class HubEndpoints
{
    public static void MapHubApi(this IEndpointRouteBuilder routes)
    {
        // V2a pairing: voucher redeem is intentionally OUTSIDE the
        // auth group. The voucher IS the auth -- a fresh agent install
        // has no cookie + no bearer; the act of presenting a valid,
        // unconsumed, unexpired voucher is what proves the caller has
        // permission to enroll. Authenticated bearer endpoints + cookie
        // endpoints still flow through the `api` group below.
        routes.MapPost("/api/enroll/redeem",
            (Magpilot.Shared.Models.EnrollmentRedeemRequest req,
             Magpilot.Hub.Auth.EnrollmentService enrol,
             HttpContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Voucher) || string.IsNullOrWhiteSpace(req.AgentName))
                    return Results.BadRequest(new { error = "voucher and agentName are required" });

                // We deliberately do NOT take the agent's URL on the
                // redeem call -- the agent will broadcast its own URL
                // on the next discovery sweep and the hub will record it
                // there. This keeps the agent name as the only identity
                // the redeem flow has to trust.
                var result = enrol.RedeemVoucher(req.Voucher, req.AgentName, agentUrl: null);
                return result.Status switch
                {
                    Magpilot.Hub.Auth.EnrollmentService.RedeemStatus.Ok =>
                        Results.Ok(new Magpilot.Shared.Models.EnrollmentRedeemResponse(result.AgentToken!)),
                    Magpilot.Hub.Auth.EnrollmentService.RedeemStatus.InvalidVoucher =>
                        Results.Json(new { error = result.Message ?? "invalid voucher" }, statusCode: StatusCodes.Status401Unauthorized),
                    // 410 Gone: the resource (this voucher) is permanently
                    // unavailable -- either expired or already consumed.
                    // Distinguishing the two in the body lets the launcher
                    // show a useful error ("ask for a fresh one" vs "looks
                    // like someone else used it").
                    Magpilot.Hub.Auth.EnrollmentService.RedeemStatus.Expired
                        or Magpilot.Hub.Auth.EnrollmentService.RedeemStatus.AlreadyConsumed =>
                        Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status410Gone),
                    _ => Results.Problem("unknown redeem result"),
                };
            });

        // V3 pairing: agent submits a claim with a self-generated
        // secret. Unauthenticated (the claim secret IS the auth
        // handle: only the agent that generated it knows it). Returns
        // the claim id + a fingerprint the launcher prints for the
        // admin to visually verify, plus the URL the launcher opens
        // in the user's browser.
        routes.MapPost("/api/enroll/claim",
            (Magpilot.Shared.Models.PairingClaimRequest req,
             Magpilot.Hub.Auth.ClaimService claims,
             HttpContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Secret) || string.IsNullOrWhiteSpace(req.AgentName))
                    return Results.BadRequest(new { error = "secret and agentName are required" });

                int claimId;
                string fingerprint;
                try
                {
                    (claimId, fingerprint) = claims.CreateClaim(req.Secret, req.AgentName);
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    // UNIQUE constraint on secret_hash. Astronomically
                    // unlikely for 256-bit CSPRNG secrets but covered
                    // for completeness.
                    return Results.Conflict(new { error = "claim secret already used; generate a new one" });
                }

                // Build the approve URL from the same scheme+host the
                // caller used so the launcher can `Process.Start(url)`
                // and land on the right SPA.
                var hubBase = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
                var approveUrl = $"{hubBase}/admin/agents?pending={claimId}";
                return Results.Ok(new Magpilot.Shared.Models.PairingClaimResponse(claimId, approveUrl, fingerprint));
            });

        // V3 pairing: long-poll the claim's status. The launcher
        // calls this in a loop with the same secret it used for
        // CreateClaim; the hub blocks server-side for up to 60s
        // waiting for Approve/Reject to fire. Returns immediately
        // on terminal status (already-decided / expired) and on the
        // long-poll timeout (status: pending -> launcher re-polls).
        // Unauthenticated: the secret is the auth handle.
        routes.MapGet("/api/enroll/claim-status",
            async (string? secret,
                   Magpilot.Hub.Auth.ClaimService claims,
                   CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(secret))
                    return Results.BadRequest(new { error = "secret query parameter required" });
                var status = await claims.AwaitStatusAsync(secret, ct);
                return Results.Ok(status);
            });

        var api = routes.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", (HttpContext ctx) =>
            Results.Ok(new { identity = ctx.User.Identity?.Name }));

        // V2b pairing: revoke a paired agent. Clears the agent's
        // per-agent bearer token + sets revoked_at. The Proxy wrapper
        // short-circuits subsequent calls to this agent with 410 Gone
        // + a "re-pair" hint. Reversible: re-enrolling via voucher
        // upserts revoked_at = NULL and writes a fresh token. 404 if
        // no such agent.
        api.MapPost("/admin/agents/{name}/revoke", (string name, AgentRegistry reg) =>
        {
            if (!reg.Revoke(name))
                return Results.NotFound(new { error = $"Unknown agent {name}" });
            return Results.Ok(reg.Get(name));
        });

        // V3 pairing: admin-side list of recent claims (pending +
        // recently-decided). Drives the "Pending pair requests"
        // section of /admin/agents in the SPA.
        api.MapGet("/admin/agents/claims",
            (Magpilot.Hub.Auth.ClaimService claims) =>
                Results.Ok(claims.ListClaims()));

        api.MapPost("/admin/agents/claims/{id:int}/approve",
            (int id, Magpilot.Hub.Auth.ClaimService claims, HttpContext ctx) =>
            {
                if (!claims.ApproveClaim(id, ctx.User.Identity?.Name))
                    return Results.NotFound(new { error = $"Claim {id} not found, already decided, or expired" });
                return Results.NoContent();
            });

        api.MapPost("/admin/agents/claims/{id:int}/reject",
            (int id, Magpilot.Hub.Auth.ClaimService claims, HttpContext ctx) =>
            {
                if (!claims.RejectClaim(id, ctx.User.Identity?.Name))
                    return Results.NotFound(new { error = $"Claim {id} not found or not pending" });
                return Results.NoContent();
            });

        // V2a pairing: mint a one-time enrollment voucher for a fresh
        // agent install. Cookie-auth-gated (this admin group requires
        // auth); a signed-in user is implicitly authorized to issue
        // vouchers. Returns 503 + a hint when the hub itself isn't
        // configured to issue vouchers yet (typically: missing
        // MAGPILOT_HUB_PUBLIC_URL or running with the dev hub bearer).
        api.MapPost("/admin/enroll/voucher",
            (Magpilot.Hub.Auth.EnrollmentService enrol, HttpContext ctx) =>
            {
                var bundle = enrol.CreateVoucher(ctx.User.Identity?.Name, out var err);
                if (bundle is null)
                    return Results.Json(new { error = err }, statusCode: StatusCodes.Status503ServiceUnavailable);
                return Results.Ok(new { encoded = bundle.Encode() });
            });

        // Hub's view of the latest released agent/launcher version. Populated
        // by ReleaseTracker (background polls of GitHub Releases). The agent
        // calls this every ~15min via its UpdatePoller and caches the result;
        // the SPA can also call it for an "update available" indicator.
        //
        // Optional ?from=X.Y.Z lets the caller report its current version so
        // the hub can compute UpdateAvailable for that specific caller. Without
        // ?from we just report what we know with UpdateAvailable=false.
        api.MapGet("/agent-version", (ReleaseCache cache, string? from) =>
        {
            var cached = cache.Get();
            if (cached is null)
            {
                // No release polled yet (or the configured repo has no
                // releases). Always return 200 with a sane default so
                // callers never have to handle 404.
                return Results.Ok(new LatestVersionInfo(
                    LatestVersion: "",
                    MinProtocol: Versioning.ProtocolVersion,
                    MaxProtocol: Versioning.ProtocolVersion,
                    UpdateAvailable: false));
            }

            var updateAvailable = false;
            if (!string.IsNullOrEmpty(from)
                && Version.TryParse(from, out var fromV)
                && Version.TryParse(cached.LatestVersion, out var latestV))
            {
                updateAvailable = latestV > fromV;
            }

            return Results.Ok(cached with { UpdateAvailable = updateAvailable });
        });

        api.MapGet("/agents", (AgentRegistry reg) => reg.List());

        // V2a pairing: the manual POST /api/agents register endpoint
        // is gone. The only way to add an agent to the registry now is
        // via the voucher-redeem flow (POST /api/enroll/redeem) -- that
        // mints the per-agent token alongside the registration entry,
        // so there's no point where the hub has a registered agent
        // with no credentials. DELETE is still useful for kicking an
        // agent out of the registry.
        api.MapDelete("/agents/{name}", (string name, AgentRegistry reg) =>
            reg.Remove(name) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/agents/discover", async (DiscoveryProber prober, CancellationToken ct) =>
        {
            await prober.ProbeOnceAsync(ct);
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
                    var resp = await http.ClientFor(name, AgentClientKind.Action).PostAsJsonAsync("api/sessions", req, ct);
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
                    var resp = await http.ClientFor(name, AgentClientKind.Action).PostAsJsonAsync($"api/sessions/{id}/adopt", req, ct);
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

        // ---- shim Phase 1+ proxies (cooperative single-owner handoff) ----
        // Pure pass-throughs; bodies and statuses are forwarded verbatim.
        // The shim docs (copilot-context/ideas/projects/magpilot-shim.md)
        // describe the wire shapes.

        api.MapGet("/agents/{name}/sessions/{id}/state",
            (string name, string id, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).GetAsync($"api/sessions/{id}/state", ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/release-request",
            (string name, string id, ReleaseRequestBody body, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/release-request", body, ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/acquire-for-host",
            (string name, string id, AcquireForHostBody body, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/acquire-for-host", body, ct);
                    return await Forward(resp);
                }));

        api.MapPost("/agents/{name}/sessions/{id}/release",
            (string name, string id, ReleaseFromHostBody body, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/release", body, ct);
                    return await Forward(resp);
                }));
        // ------------------------------------------------------------------

        api.MapPost("/agents/{name}/sessions/{id}/yolo",
            (string name, string id, YoloRequest body, AgentHttpClient http, AgentRegistry reg, CancellationToken ct) =>
                Proxy(name, reg, async () =>
                {
                    var resp = await http.ClientFor(name).PostAsJsonAsync($"api/sessions/{id}/yolo", body, ct);
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
                    var client = http.ClientFor(name, AgentClientKind.Stream);
                    var resp = await client.PostAsJsonAsync("api/quick-prompt", req, ct);
                    return await Forward(resp);
                }));

        // ---- SSE proxy ---------------------------------------------------------
        api.MapGet("/agents/{name}/sessions/{id}/stream",
            async (string name, string id, AgentHttpClient http, AgentRegistry reg, HttpContext ctx, CancellationToken ct) =>
            {
                // SSE uses the no-timeout client; the connection lifetime is controlled by the SPA.
                using var client = http.ClientFor(name, AgentClientKind.Stream);
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
        // Short-circuit revoked agents BEFORE attempting the call.
        // The agent's token has been cleared from the registry so the
        // call would fail with 401 anyway, but the user deserves a
        // clear "revoked; re-pair with a fresh voucher" message
        // instead of chasing a phantom auth issue.
        if (reg.IsRevoked(name))
        {
            return Results.Json(
                new { error = $"Agent {name} is revoked. Generate a fresh enrollment bundle on /admin/enroll and run `magpilot --magpilot-pair=<bundle>` on the machine to re-enroll.", agent = name, revoked = true },
                statusCode: StatusCodes.Status410Gone);
        }

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
