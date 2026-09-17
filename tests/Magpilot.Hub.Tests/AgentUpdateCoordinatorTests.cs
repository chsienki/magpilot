using System.Net;
using System.Net.Http.Json;
using Magpilot.Hub.Agents;
using Magpilot.Hub.Updates;
using Magpilot.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Hub.Tests;

public sealed class AgentUpdateCoordinatorTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_refreshes_release_and_pushes_each_agent()
    {
        var directory = Directory.CreateTempSubdirectory("magpilot-agent-update-test");
        try
        {
            var registry = CreateRegistry(directory.FullName);
            registry.Upsert("alpha", "http://alpha.test", "agent-token", online: true);
            HttpRequestMessage? agentRequest = null;
            var agentFactory = new StubHttpClientFactory(request =>
            {
                agentRequest = request;
                return JsonResponse(new AgentVersionStatus(
                    Version: "1.0.0",
                    ProtocolVersion: 1,
                    LatestVersion: "2.0.0",
                    MinProtocol: 1,
                    MaxProtocol: 1,
                    UpdateAvailable: true,
                    LastCheckedAt: DateTimeOffset.UtcNow));
            });
            var releaseTracker = new ReleaseTracker(
                new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"tag_name":"v2.0.0","assets":[]}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                }),
                new ReleaseCache(),
                NullLogger<ReleaseTracker>.Instance,
                new ConfigurationBuilder().Build());
            var coordinator = new AgentUpdateCoordinator(
                new AgentHttpClient(agentFactory, registry),
                registry,
                releaseTracker,
                NullLogger<AgentUpdateCoordinator>.Instance);

            var result = await coordinator.CheckForUpdatesAsync(registry.List());

            Assert.Equal("2.0.0", result.LatestVersion);
            var report = Assert.Single(result.Agents);
            Assert.Equal("alpha", report.AgentName);
            Assert.True(report.Status!.UpdateAvailable);
            Assert.NotNull(agentRequest);
            Assert.Equal(HttpMethod.Post, agentRequest.Method);
            Assert.Equal("/api/version/refresh", agentRequest.RequestUri!.AbsolutePath);
            Assert.Equal("agent-token", agentRequest.Headers.Authorization?.Parameter);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetStatusesAsync_falls_back_for_older_agents()
    {
        var directory = Directory.CreateTempSubdirectory("magpilot-agent-version-test");
        try
        {
            var registry = CreateRegistry(directory.FullName);
            registry.Upsert("legacy", "http://legacy.test", "agent-token", online: true);
            var agentFactory = new StubHttpClientFactory(request =>
            {
                return request.RequestUri!.AbsolutePath switch
                {
                    "/api/version/status" => new HttpResponseMessage(HttpStatusCode.NotFound),
                    "/api/version" => JsonResponse(new VersionInfo("1.0.0", 1)),
                    "/api/version/latest" => JsonResponse(new LatestVersionInfo(
                        LatestVersion: "2.0.0",
                        MinProtocol: 1,
                        MaxProtocol: 1,
                        UpdateAvailable: true)),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            });
            var coordinator = new AgentUpdateCoordinator(
                new AgentHttpClient(agentFactory, registry),
                registry,
                CreateUnusedReleaseTracker(),
                NullLogger<AgentUpdateCoordinator>.Instance);

            var reports = await coordinator.GetStatusesAsync(registry.List());

            var status = Assert.Single(reports).Status;
            Assert.NotNull(status);
            Assert.Equal("1.0.0", status!.Version);
            Assert.Equal("2.0.0", status.LatestVersion);
            Assert.True(status.UpdateAvailable);
            Assert.False(status.RefreshSupported);
            Assert.Null(status.LastCheckedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_keeps_reachable_agent_online_when_refresh_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("magpilot-agent-refresh-test");
        try
        {
            var registry = CreateRegistry(directory.FullName);
            registry.Upsert("alpha", "http://alpha.test", "agent-token", online: true);
            var agentFactory = new StubHttpClientFactory(request =>
            {
                if (request.Method == HttpMethod.Post)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

                return JsonResponse(new AgentVersionStatus(
                    Version: "1.0.0",
                    ProtocolVersion: 1,
                    LatestVersion: "2.0.0",
                    MinProtocol: 1,
                    MaxProtocol: 1,
                    UpdateAvailable: true,
                    LastCheckedAt: DateTimeOffset.UtcNow));
            });
            var coordinator = new AgentUpdateCoordinator(
                new AgentHttpClient(agentFactory, registry),
                registry,
                CreateReleaseTracker("2.0.0"),
                NullLogger<AgentUpdateCoordinator>.Instance);

            var result = await coordinator.CheckForUpdatesAsync(registry.List());

            var report = Assert.Single(result.Agents);
            Assert.NotNull(report.Status);
            Assert.Contains("503", report.Error);
            Assert.True(registry.Get("alpha")!.Online);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(recursive: true);
        }
    }

    private static AgentRegistry CreateRegistry(string dataDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:DataDir"] = dataDirectory,
            })
            .Build();
        return new AgentRegistry(configuration, NullLogger<AgentRegistry>.Instance);
    }

    private static ReleaseTracker CreateUnusedReleaseTracker() =>
        new(
            new StubHttpClientFactory(_ => throw new InvalidOperationException()),
            new ReleaseCache(),
            NullLogger<ReleaseTracker>.Instance,
            new ConfigurationBuilder().Build());

    private static ReleaseTracker CreateReleaseTracker(string version) =>
        new(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"tag_name":"v{{version}}","assets":[]}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            }),
            new ReleaseCache(),
            NullLogger<ReleaseTracker>.Instance,
            new ConfigurationBuilder().Build());

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value),
        };

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(responseFactory));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
