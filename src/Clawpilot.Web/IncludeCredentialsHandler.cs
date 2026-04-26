using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Clawpilot.Web;

/// <summary>
/// Forces the browser fetch API to send cookies on every request so the hub's
/// cookie-based auth works for the SPA.
/// </summary>
internal sealed class IncludeCredentialsHandler : DelegatingHandler
{
    public IncludeCredentialsHandler() : base(new HttpClientHandler()) { }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        // SSE endpoints must stream — the default WASM fetch buffers the entire
        // response, which never completes for an open SSE stream.
        if (request.RequestUri is { } uri && uri.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal))
        {
            request.SetBrowserResponseStreamingEnabled(true);
        }
        return base.SendAsync(request, ct);
    }
}
