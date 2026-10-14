using System.Net;
using Magpilot.Host;
using Magpilot.Shared.Models;
using Xunit;

namespace Magpilot.Host.Tests;

public sealed class AutomaticHostAcquisitionTests
{
    [Fact]
    public async Task Rejected_acquisition_never_requests_another_clients_release()
    {
        var notified = false;
        var conflict = new HttpRequestException("Already held", null, HttpStatusCode.Conflict);

        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            AutomaticHostAcquisition.AcquireThenNotifyAsync(
                () => Task.FromException<SessionStateInfo>(conflict),
                () => { notified = true; return Task.CompletedTask; },
                _ => { }));

        Assert.Same(conflict, actual);
        Assert.False(notified);
    }

    [Fact]
    public async Task Accepted_acquisition_notifies_only_after_it_has_the_lease()
    {
        var steps = new List<string>();
        var state = State();

        var result = await AutomaticHostAcquisition.AcquireThenNotifyAsync(
            () => { steps.Add("acquire"); return Task.FromResult(state); },
            () => { steps.Add("notify"); return Task.CompletedTask; },
            _ => { });

        Assert.Same(state, result);
        Assert.Equal(["acquire", "notify"], steps);
    }

    [Fact]
    public async Task Notification_failure_keeps_the_acquired_lease()
    {
        var state = State();
        var failure = new IOException("Stream closed");
        Exception? reported = null;

        var result = await AutomaticHostAcquisition.AcquireThenNotifyAsync(
            () => Task.FromResult(state),
            () => Task.FromException(failure),
            ex => reported = ex);

        Assert.Same(state, result);
        Assert.Same(failure, reported);
    }

    private static SessionStateInfo State() => new(
        new SessionInfo("session", SessionState.Locked, null, null, null, null, null, null, null),
        SessionOwner.Host, 42, Guid.NewGuid(), SessionActivity.Idle, null, null, null, []);
}
