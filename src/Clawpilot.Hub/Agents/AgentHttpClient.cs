namespace Clawpilot.Hub.Agents;

/// <summary>
/// HTTP client wrapper that proxies requests to a per-host agent, applying
/// that agent's bearer token automatically.
/// </summary>
public sealed class AgentHttpClient
{
    private readonly IHttpClientFactory _factory;
    private readonly AgentRegistry _registry;

    public AgentHttpClient(IHttpClientFactory factory, AgentRegistry registry)
    {
        _factory = factory;
        _registry = registry;
    }

    public HttpClient ClientFor(string agentName)
    {
        var info = _registry.Get(agentName)
            ?? throw new KeyNotFoundException($"Unknown agent {agentName}");
        var client = _factory.CreateClient("agent");
        client.BaseAddress = new Uri(info.Url.TrimEnd('/') + "/");
        var token = _registry.GetToken(agentName);
        if (token is not null)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
