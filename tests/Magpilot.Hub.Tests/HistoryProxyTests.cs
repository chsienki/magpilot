using Magpilot.Hub.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Magpilot.Hub.Tests;

public sealed class HistoryProxyTests
{
    [Theory]
    [InlineData("", "api/sessions/session-id/history")]
    [InlineData(
        "?before=150&limit=50",
        "api/sessions/session-id/history?before=150&limit=50")]
    [InlineData(
        "?tail=25",
        "api/sessions/session-id/history?tail=25")]
    public void BuildAgentHistoryPath_PreservesPagingQuery(
        string query,
        string expected)
    {
        var path = HubEndpoints.BuildAgentHistoryPath(
            "session-id",
            new QueryString(query));

        Assert.Equal(expected, path);
    }
}
