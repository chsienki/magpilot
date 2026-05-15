namespace Magpilot.Hub.Agents;

/// <summary>
/// Which named HttpClient flavor to use when talking to an agent. Each has a
/// different timeout budget; see Program.cs for the values.
/// <list type="bullet">
///   <item><c>Read</c> -- short (10s default). Use for fast aggregation calls
///     (e.g. GET /api/sessions) where one slow agent shouldn't stall the SPA.</item>
///   <item><c>Action</c> -- medium (90s default). Use for ACP-driving mutations
///     (session/new, session/load) that can legitimately take tens of seconds.</item>
///   <item><c>Stream</c> -- infinite. Use for SSE / quick-prompt where a turn
///     can run for many minutes.</item>
/// </list>
/// </summary>
public enum AgentClientKind { Read, Action, Stream }

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

    public HttpClient ClientFor(string agentName, AgentClientKind kind = AgentClientKind.Read)
    {
        var info = _registry.Get(agentName)
            ?? throw new KeyNotFoundException($"Unknown agent {agentName}");
        var name = kind switch
        {
            AgentClientKind.Stream => "agent-stream",
            AgentClientKind.Action => "agent-action",
            _ => "agent",
        };
        var client = _factory.CreateClient(name);
        client.BaseAddress = new Uri(info.Url.TrimEnd('/') + "/");
        var token = _registry.GetToken(agentName);
        if (token is not null)
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
