using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Magpilot.Hub.Auth;

/// <summary>
/// Minimal hand-rolled GitHub OAuth flow. We do this manually instead of using
/// AspNet.Security.OAuth so we can enforce the username allowlist in one place.
/// </summary>
public static class GitHubOAuth
{
    public static void MapGitHubOAuth(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/login", (HubAuthOptions opts, HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(opts.OAuthClientId))
            {
                if (Environment.GetEnvironmentVariable("MAGPILOT_DEV_BYPASS_AUTH") == "true")
                {
                    var ret = ctx.Request.Query["ReturnUrl"].ToString();
                    var devUrl = string.IsNullOrEmpty(ret) ? "/dev-login" : $"/dev-login?ReturnUrl={Uri.EscapeDataString(ret)}";
                    return Results.Redirect(devUrl);
                }
                return Results.Problem("OAUTH_CLIENT_ID not configured");
            }
            var state = Guid.NewGuid().ToString("N");
            ctx.Response.Cookies.Append("oauth_state", state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10),
            });
            // Stash the original ReturnUrl in a sibling cookie so the
            // callback can bounce the user back to where they started --
            // critical for the multi-subdomain shared-cookie setup, where
            // a 401 at magnus.home.example bounces here for OAuth and we
            // want to send them back to magnus.home, not to magpilot.home.
            // Sibling cookie (not GitHub state param) keeps it server-side
            // private and out of any logs that capture the OAuth URL.
            var ret0 = ctx.Request.Query["ReturnUrl"].ToString();
            if (!string.IsNullOrEmpty(ret0))
            {
                ctx.Response.Cookies.Append("oauth_return", ret0, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = ctx.Request.IsHttps,
                    MaxAge = TimeSpan.FromMinutes(10),
                });
            }
            var redirect = $"{ctx.Request.Scheme}://{ctx.Request.Host}/oauth/callback";
            var url = $"https://github.com/login/oauth/authorize?client_id={opts.OAuthClientId}&scope=read:user&state={state}&redirect_uri={Uri.EscapeDataString(redirect)}";
            return Results.Redirect(url);
        });

        routes.MapGet("/oauth/callback", async (string code, string state, HubAuthOptions opts, HttpContext ctx, IHttpClientFactory http) =>
        {
            var stateCookie = ctx.Request.Cookies["oauth_state"];
            if (string.IsNullOrEmpty(stateCookie) || stateCookie != state)
                return Results.BadRequest("invalid oauth state");
            ctx.Response.Cookies.Delete("oauth_state");

            using var client = http.CreateClient("oauth");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var tokenResp = await client.PostAsJsonAsync("https://github.com/login/oauth/access_token", new
            {
                client_id = opts.OAuthClientId,
                client_secret = opts.OAuthClientSecret,
                code,
            });
            if (!tokenResp.IsSuccessStatusCode)
                return Results.Problem("github token exchange failed");
            var token = await tokenResp.Content.ReadFromJsonAsync<TokenResponse>();
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                return Results.Problem("github returned no token");

            using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            userReq.Headers.UserAgent.ParseAdd("magpilot-hub");
            var userResp = await client.SendAsync(userReq);
            if (!userResp.IsSuccessStatusCode)
                return Results.Problem("github /user failed");
            var user = await userResp.Content.ReadFromJsonAsync<GhUser>();
            if (user is null || string.IsNullOrEmpty(user.Login))
                return Results.Problem("github /user empty");

            if (!opts.AllowedGitHubUsers.Contains(user.Login, StringComparer.OrdinalIgnoreCase))
                return Results.StatusCode(403);

            var claims = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, user.Login),
                new Claim("auth_kind", "github_oauth"),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claims),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

            // Recover the original ReturnUrl set in /login, with sanity checks
            // mirroring /dev-login so a stale or hostile cookie can't bounce
            // the user to a JSON endpoint or an off-site URL.
            var ret = ctx.Request.Cookies["oauth_return"] ?? "";
            ctx.Response.Cookies.Delete("oauth_return");

            // Allow either a relative path (with the same /api guard as
            // /dev-login) OR an absolute URL whose host falls under the
            // configured cookie domain (so multi-subdomain shared cookies
            // can land back at sibling SPAs).
            string target = "/";
            if (!string.IsNullOrEmpty(ret))
            {
                if (ret.StartsWith('/') && !ret.StartsWith("//", StringComparison.Ordinal)
                    && !ret.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    target = ret;
                }
                else if (Uri.TryCreate(ret, UriKind.Absolute, out var uri)
                         && !string.IsNullOrEmpty(opts.CookieDomain)
                         && IsHostInDomain(uri.Host, opts.CookieDomain))
                {
                    target = ret;
                }
            }
            return Results.Redirect(target);
        });

        routes.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        routes.MapGet("/dev-login", async (HubAuthOptions opts, HttpContext ctx) =>
        {
            if (Environment.GetEnvironmentVariable("MAGPILOT_DEV_BYPASS_AUTH") != "true")
                return Results.NotFound();
            var login = opts.AllowedGitHubUsers.FirstOrDefault() ?? "dev";
            var claims = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, login),
                new Claim("auth_kind", "dev_bypass"),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claims),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1) });
            var ret = ctx.Request.Query["ReturnUrl"].ToString();
            // Refuse non-SPA targets (must be a relative path that isn't /api/...)
            // so a stale link can't dump the user on a JSON endpoint.
            if (string.IsNullOrEmpty(ret) || !ret.StartsWith('/') || ret.StartsWith("//", StringComparison.Ordinal)
                || ret.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
                ret = "/";
            return Results.Redirect(ret);
        });
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record GhUser([property: JsonPropertyName("login")] string? Login);

    /// <summary>
    /// Returns true if <paramref name="host"/> falls under the cookie
    /// <paramref name="domain"/>. Domain is canonically a leading-dot
    /// value (e.g. ".home.sienkiewi.cz"); a host like
    /// "magnus.home.sienkiewi.cz" matches but "evil-home.sienkiewi.cz"
    /// does not (suffix match must be on the dot boundary).
    /// </summary>
    private static bool IsHostInDomain(string host, string domain)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(domain)) return false;
        var d = domain.StartsWith('.') ? domain : "." + domain;
        var h = "." + host;
        return h.EndsWith(d, StringComparison.OrdinalIgnoreCase);
    }
}
