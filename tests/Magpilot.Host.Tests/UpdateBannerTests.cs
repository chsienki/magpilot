using System.Net;
using System.Text;
using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public class UpdateBannerTests
{
    [Fact]
    public async Task Sends_launcher_version_and_prints_available_update()
    {
        var handler = new RecordingHandler(
            """{"latestVersion":"0.1.34","minProtocol":1,"maxProtocol":1,"updateAvailable":true}""");
        using var http = new HttpClient(handler);
        using var error = new StringWriter();

        await UpdateBanner.MaybePrintAsync(http, error, "http://agent:5099/", "0.1.33");

        Assert.Equal(
            "http://agent:5099/api/version/latest?from=0.1.33",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal(
            "magpilot: 0.1.34 available (current: 0.1.33). " +
            "run `magpilot --magpilot-update` to install.",
            error.ToString().Trim());
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }
}
