using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Clawpilot.Hub.Auth;

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
                return Results.Problem("OAUTH_CLIENT_ID not configured");
            var state = Guid.NewGuid().ToString("N");
            ctx.Response.Cookies.Append("oauth_state", state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = ctx.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10),
            });
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
            userReq.Headers.UserAgent.ParseAdd("clawpilot-hub");
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
            return Results.Redirect("/");
        });

        routes.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        routes.MapGet("/dev-login", async (HubAuthOptions opts, HttpContext ctx) =>
        {
            if (Environment.GetEnvironmentVariable("CLAWPILOT_DEV_BYPASS_AUTH") != "true")
                return Results.NotFound();
            var login = opts.AllowedGitHubUsers.FirstOrDefault() ?? "dev";
            var claims = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, login),
                new Claim("auth_kind", "dev_bypass"),
            ], CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claims),
                new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1) });
            return Results.Redirect("/");
        });
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record GhUser([property: JsonPropertyName("login")] string? Login);
}
