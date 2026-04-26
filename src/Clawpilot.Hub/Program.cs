using Clawpilot.Hub.Agents;
using Clawpilot.Hub.Api;
using Clawpilot.Hub.Auth;
using Clawpilot.Hub.Discovery;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<AgentHttpClient>();
builder.Services.AddSingleton<DiscoveryProber>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscoveryProber>());
builder.Services.AddHttpClient("agent");
builder.Services.AddHttpClient("oauth");
builder.Services.AddHubAuth(builder.Configuration);

var app = builder.Build();

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

// SPA fallback: any unmatched, non-API path -> index.html.
app.MapFallbackToFile("index.html");

app.Run();
