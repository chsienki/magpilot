using Magpilot.Agent.Acp;
using Magpilot.Agent.Api;
using Magpilot.Agent.Discovery;
using Magpilot.Agent.Logging;
using Magpilot.Agent.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

// Load environment variables from magpilot.env BEFORE the host builder
// reads its configuration. The installed layout puts the env file at
// <install>\config\magpilot.env (sibling of the agent's directory); the
// installer wrote it from the wizard's settings page. By doing this in
// the agent itself, the scheduled task can launch Magpilot.Agent.exe
// directly with no powershell wrapper -- which is what lets us be a
// WinExe with zero visible console window. MAGPILOT_ENV_FILE can
// override the path (useful for dev).
EnvFileLoader.Load();

var builder = WebApplication.CreateBuilder(args);

// If nothing else has set ASPNETCORE_URLS by now (env file, real env
// var, command line), bind to all interfaces on the standard agent
// port. The installed deployment relied on a powershell wrapper to set
// this; baking the default in keeps the wrapper unnecessary.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    && !args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase))
    && string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5099");
}

// Forward Warning+ logs to the central hub. No-ops if MAGPILOT_HUB_URL +
// MAGPILOT_HUB_BEARER aren't set, so dev runs without a hub still work.
builder.Logging.AddProvider(new HubLoggerProvider());

builder.Services.AddSingleton<FlavorCapabilities>();
builder.Services.AddSingleton<AcpFlavorPool>();
builder.Services.AddSingleton<AcpSessionManager>();
builder.Services.AddSingleton<SessionScanner>();
builder.Services.AddSingleton<HostOwnership>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostOwnership>());
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<HistoryReader>();
builder.Services.AddSingleton<Magpilot.Agent.Update.LatestVersionCache>();
builder.Services.AddHttpClient("hub-update", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHostedService<Magpilot.Agent.Update.UpdatePoller>();
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

/// <summary>
/// Loads KEY=VALUE pairs from <c>magpilot.env</c> into the current
/// process's environment, before the WebApplication builder is
/// constructed. Lines beginning with '#' (after trim) and blank lines
/// are ignored. Existing environment variables are preserved (the file
/// is a default, not an override) so dev runs that already have
/// MAGPILOT_* set in the shell win.
///
/// Search order:
/// 1. <c>MAGPILOT_ENV_FILE</c> environment variable (explicit override).
/// 2. <c>&lt;exe-dir&gt;/../config/magpilot.env</c> (installer layout:
///    <c>C:\Program Files\Magpilot\agent\Magpilot.Agent.exe</c> +
///    <c>C:\Program Files\Magpilot\config\magpilot.env</c>).
///
/// Silently no-ops if no file is found, so <c>dotnet run</c> from a
/// dev shell without an env file just uses the shell's exported vars.
/// </summary>
internal static class EnvFileLoader
{
    public static void Load()
    {
        var path = FindEnvFile();
        if (path is null) return;
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                if (Environment.GetEnvironmentVariable(key) is not null) continue;
                Environment.SetEnvironmentVariable(key, val);
            }
        }
        catch
        {
            // Best-effort load. If the file is malformed or unreadable
            // the agent should still start with whatever env vars exist.
        }
    }

    private static string? FindEnvFile()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MAGPILOT_ENV_FILE");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;
        var exeDir = AppContext.BaseDirectory;
        var installed = Path.GetFullPath(Path.Combine(exeDir, "..", "config", "magpilot.env"));
        if (File.Exists(installed)) return installed;
        return null;
    }
}

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
