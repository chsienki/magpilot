using Clawpilot.UI.Abstractions;

namespace Clawpilot.Web;

/// <summary>
/// Web shell auth provider: hub IS the origin, cookie auth from GitHub OAuth.
/// </summary>
internal sealed class WebHubAuthProvider(IConfiguration cfg) : IHubAuthProvider
{
    public string HubBaseUrl => cfg["HubBaseUrl"] ?? "/";
    public string? BearerToken => null;
    public bool UseCookieAuth => true;
}
