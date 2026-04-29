namespace Magpilot.UI.Abstractions;

/// <summary>
/// Resolves the hub base URL and authentication credentials. Implementations
/// differ per shell: web (Magpilot.Web) uses the current origin and cookie auth;
/// MAUI shell uses a configured URL and a bearer token from secure storage.
/// </summary>
public interface IHubAuthProvider
{
    string HubBaseUrl { get; }
    string? BearerToken { get; }
    bool UseCookieAuth { get; }
}
