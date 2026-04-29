using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.JSInterop;

namespace Magpilot.Web;

/// <summary>
/// Forces the browser fetch API to send cookies on every request so the hub's
/// cookie-based auth works for the SPA. Also bounces to /login on 401 so an
/// expired cookie produces a clean re-auth flow rather than a stuck UI.
/// </summary>
internal sealed class IncludeCredentialsHandler(IJSRuntime js) : DelegatingHandler(new HttpClientHandler())
{
    private int _redirected;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        // SSE endpoints must stream — the default WASM fetch buffers the entire
        // response, which never completes for an open SSE stream.
        if (request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal))
        {
            request.SetBrowserResponseStreamingEnabled(true);
        }

        var resp = await base.SendAsync(request, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && request.RequestUri is { } u
            && u.AbsolutePath.StartsWith("/api", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _redirected, 1) == 0)
        {
            // ReturnUrl must point at the SPA route the user is on, NOT the API
            // path that 401'd — otherwise OAuth bounces them back to a JSON endpoint.
            var ret = await js.InvokeAsync<string>("eval", "location.pathname + location.search");
            if (string.IsNullOrEmpty(ret)) ret = "/";
            var nav = $"/login?ReturnUrl={Uri.EscapeDataString(ret)}";
            // Fire-and-forget; we still return the 401 to the caller.
            _ = js.InvokeVoidAsync("location.assign", nav).AsTask();
        }

        return resp;
    }
}
