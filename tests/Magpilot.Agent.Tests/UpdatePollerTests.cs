using System.Net;
using System.Net.Http.Json;
using Magpilot.Agent.Update;
using Magpilot.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class UpdatePollerTests
{
    [Fact]
    public async Task RefreshAsync_fetches_hub_release_and_updates_cache()
    {
        HttpRequestMessage? observedRequest = null;
        var response = new LatestVersionInfo(
            LatestVersion: "99.0.0",
            MinProtocol: 1,
            MaxProtocol: 2,
            UpdateAvailable: true);
        var factory = new StubHttpClientFactory(request =>
        {
            observedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response),
            };
        });
        var cache = new LatestVersionCache();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hub:Url"] = "http://hub.test",
                ["Hub:Bearer"] = "hub-token",
            })
            .Build();
        var poller = new UpdatePoller(
            factory,
            cache,
            NullLogger<UpdatePoller>.Instance,
            config);

        var status = await poller.RefreshAsync();

        Assert.NotNull(observedRequest);
        Assert.Equal(
            $"/api/agent-version?from={Versioning.AssemblyVersion}",
            observedRequest.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer", observedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("hub-token", observedRequest.Headers.Authorization?.Parameter);
        Assert.Equal("99.0.0", status.LatestVersion);
        Assert.True(status.UpdateAvailable);
        Assert.NotNull(status.LastCheckedAt);
    }

    [Fact]
    public async Task RefreshAsync_fails_explicitly_when_hub_is_not_configured()
    {
        var poller = new UpdatePoller(
            new StubHttpClientFactory(_ => throw new InvalidOperationException()),
            new LatestVersionCache(),
            NullLogger<UpdatePoller>.Instance,
            new ConfigurationBuilder().Build());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => poller.RefreshAsync());

        Assert.Contains("MAGPILOT_HUB_URL", error.Message);
    }

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(responseFactory))
            {
                BaseAddress = new Uri("http://hub.test"),
            };
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
