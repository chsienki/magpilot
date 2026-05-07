using Magpilot.Agent.Acp;
using Magpilot.Agent.Api;
using Magpilot.Agent.Discovery;
using Magpilot.Agent.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<FlavorCapabilities>();
builder.Services.AddSingleton<AcpFlavorPool>();
builder.Services.AddSingleton<AcpSessionManager>();
builder.Services.AddSingleton<SessionScanner>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<HistoryReader>();
builder.Services.AddHostedService<DiscoveryResponder>();
builder.Services.AddHostedService<AcpStarter>();

var token = builder.Configuration["Agent:Token"]
    ?? Environment.GetEnvironmentVariable("MAGPILOT_AGENT_TOKEN")
    ?? "dev-token";

builder.Services.AddSingleton(new BearerOptions(token));
builder.Services.AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, BearerHandler>("Bearer", _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
app.MapAgentApi();
app.Run();

internal sealed record BearerOptions(string Token);

internal sealed class BearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> opts,
    ILoggerFactory lf,
    UrlEncoder enc,
    BearerOptions cfg) : AuthenticationHandler<AuthenticationSchemeOptions>(opts, lf, enc)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (auth[prefix.Length..] != cfg.Token)
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token"));
        var id = new ClaimsIdentity([new Claim(ClaimTypes.Name, "hub")], "Bearer");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(id), "Bearer")));
    }
}

internal sealed class AcpStarter(
    AcpFlavorPool pool,
    AcpSessionManager mgr,
    ILoggerFactory loggerFactory,
    ILogger<AcpStarter> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        log.LogInformation("Starting default ACP child process...");
        // Eagerly start the default flavor and pre-register it so the pool
        // doesn't try to spawn a duplicate on first use.
        var client = new AcpClient(loggerFactory.CreateLogger<AcpClient>(),
            AcpFlavor.Default.Exe, AcpFlavor.Default.Args);
        await client.StartAsync(ct);
        await pool.RegisterAsync(AcpFlavor.Default, client);
        // Keep mgr alive (it subscribes to pool events in its constructor).
        _ = mgr;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
