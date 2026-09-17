using System.Net;
using System.Text;
using Magpilot.Hub.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Hub.Tests;

public sealed class ReleaseTrackerTests
{
    [Fact]
    public async Task RefreshAsync_updates_release_cache_immediately()
    {
        var factory = new StubHttpClientFactory(request =>
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/version.json", StringComparison.Ordinal)
                ? """{"version":"99.0.0","minProtocol":2,"maxProtocol":3}"""
                : """
                  {
                    "tag_name": "v99.0.0",
                    "assets": [
                      {
                        "name": "version.json",
                        "browser_download_url": "https://download.test/version.json"
                      }
                    ]
                  }
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });
        var cache = new ReleaseCache();
        var tracker = new ReleaseTracker(
            factory,
            cache,
            NullLogger<ReleaseTracker>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Updates:ReleaseRepo"] = "owner/repo",
                })
                .Build());

        var result = await tracker.RefreshAsync();

        Assert.Equal("99.0.0", result.LatestVersion);
        Assert.Equal(2, result.MinProtocol);
        Assert.Equal(3, result.MaxProtocol);
        Assert.Equal(result, cache.Get());
    }

    [Fact]
    public async Task RefreshAsync_reports_missing_published_release()
    {
        var tracker = new ReleaseTracker(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
            new ReleaseCache(),
            NullLogger<ReleaseTracker>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Updates:ReleaseRepo"] = "owner/repo",
                })
                .Build());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracker.RefreshAsync());

        Assert.Contains("releases/latest returned 404", error.Message);
    }

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
