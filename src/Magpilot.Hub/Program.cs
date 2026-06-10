using Magpilot.Hub.Agents;
using Magpilot.Hub.Api;
using Magpilot.Hub.Auth;
using Magpilot.Hub.Discovery;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<AgentHttpClient>();
builder.Services.AddSingleton<DiscoveryProber>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscoveryProber>());
builder.Services.AddSingleton<Magpilot.Hub.Logging.LogStore>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Magpilot.Hub.Logging.LogStore>());
// Short timeout for read-only control-plane calls (e.g. listing sessions)
// so a single dead agent can't stall SPA aggregation.
var agentTimeoutSec = builder.Configuration.GetValue("Hub:AgentHttpTimeoutSec", 10);
builder.Services.AddHttpClient("agent", c => c.Timeout = TimeSpan.FromSeconds(agentTimeoutSec));
// Longer budget for mutating calls that drive ACP (session/new, session/load).
// ACP's own session/new defaults to 120s and session/load to 300s; this client
// must outlive the typical ACP wait or the hub will return 502 to the SPA and
// mark a perfectly healthy agent offline. Tunable via Hub:AgentActionTimeoutSec.
var agentActionTimeoutSec = builder.Configuration.GetValue("Hub:AgentActionTimeoutSec", 90);
builder.Services.AddHttpClient("agent-action", c => c.Timeout = TimeSpan.FromSeconds(agentActionTimeoutSec));
builder.Services.AddHttpClient("agent-stream", c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("releases", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Magpilot.Hub.Updates.ReleaseCache>();
builder.Services.AddHostedService<Magpilot.Hub.Updates.ReleaseTracker>();
builder.Services.AddHttpClient("oauth");
builder.Services.AddHubAuth(builder.Configuration);
builder.Services.AddSingleton<Magpilot.Hub.Auth.EnrollmentService>();
builder.Services.AddSingleton<Magpilot.Hub.Auth.ClaimService>();
builder.Services.AddHostedService<Magpilot.Hub.Discovery.PairingDiscoveryResponder>();

// Honor X-Forwarded-* from the configured reverse proxy (e.g. NPM at .149 in
// my home setup). Trust only the proxy CIDR / IPs supplied via config.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
    var proxyList = builder.Configuration["Hub:TrustedProxies"]
        ?? Environment.GetEnvironmentVariable("MAGPILOT_HUB_TRUSTED_PROXIES");
    if (!string.IsNullOrWhiteSpace(proxyList))
    {
        foreach (var entry in proxyList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPAddress.TryParse(entry, out var ip))
                opts.KnownProxies.Add(ip);
        }
    }
});

var app = builder.Build();

app.UseForwardedHeaders();

// SPA assets live in wwwroot/. The build copies the Web project's published output here.
app.UseBlazorFrameworkFiles();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
app.MapGitHubOAuth();
app.MapHubApi();
app.MapLogApi();

// SPA fallback: any unmatched, non-API path -> index.html.
app.MapFallbackToFile("index.html");

app.Run();
