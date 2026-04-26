using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Clawpilot.Hub.Auth;

public sealed record HubAuthOptions(string PhoneBearer, IReadOnlyList<string> AllowedGitHubUsers, string? OAuthClientId, string? OAuthClientSecret);

public static class HubAuthExtensions
{
    public const string BearerScheme = "PhoneBearer";
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string CombinedPolicy = "ClawpilotUser";

    public static IServiceCollection AddHubAuth(this IServiceCollection services, IConfiguration config)
    {
        var opts = new HubAuthOptions(
            PhoneBearer: config["Hub:PhoneBearer"]
                ?? Environment.GetEnvironmentVariable("CLAWPILOT_HUB_BEARER")
                ?? "dev-bearer",
            AllowedGitHubUsers: (config["Hub:OAuthAllowedUsers"]
                ?? Environment.GetEnvironmentVariable("OAUTH_ALLOWED_GITHUB_USERS")
                ?? "chsienki").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            OAuthClientId: config["Hub:OAuthClientId"] ?? Environment.GetEnvironmentVariable("OAUTH_CLIENT_ID"),
            OAuthClientSecret: config["Hub:OAuthClientSecret"] ?? Environment.GetEnvironmentVariable("OAUTH_CLIENT_SECRET"));
        services.AddSingleton(opts);

        var auth = services.AddAuthentication(o =>
            {
                o.DefaultScheme = "Multi";
            })
            .AddPolicyScheme("Multi", "Multi", o =>
            {
                o.ForwardDefaultSelector = ctx =>
                {
                    var hdr = ctx.Request.Headers.Authorization.ToString();
                    return hdr.StartsWith("Bearer ", StringComparison.Ordinal) ? BearerScheme : CookieScheme;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, PhoneBearerHandler>(BearerScheme, _ => { })
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = "clawpilot_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.LoginPath = "/login";
                o.LogoutPath = "/logout";
            });

        services.AddAuthorization(o =>
        {
            o.AddPolicy(CombinedPolicy, p => p.RequireAuthenticatedUser());
            o.DefaultPolicy = o.GetPolicy(CombinedPolicy)!;
        });
        return services;
    }
}

internal sealed class PhoneBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> opts,
    ILoggerFactory lf,
    UrlEncoder enc,
    HubAuthOptions cfg) : AuthenticationHandler<AuthenticationSchemeOptions>(opts, lf, enc)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (auth[prefix.Length..] != cfg.PhoneBearer)
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer"));
        var id = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, cfg.AllowedGitHubUsers.FirstOrDefault() ?? "user"),
            new Claim("auth_kind", "phone_bearer"),
        ], HubAuthExtensions.BearerScheme);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(id), HubAuthExtensions.BearerScheme)));
    }
}
