#pragma warning disable GHCP001

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Magpilot.Agent.Runtime.Sdk;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class SdkPermissionBrokerTests
{
    [Fact]
    public async Task Yolo_auto_approves_an_ordinary_request()
    {
        var yolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
        yolo.Set("session-1", enabled: true);
        var broker = CreateBroker(yolo);
        var published = new List<StreamEvent>();
        var handler = broker.CreateHandler(
            "session-1",
            (_, evt) => published.Add(evt),
            CancellationToken.None);

        var decision = await handler(
            ShellRequest(),
            new PermissionInvocation { SessionId = "session-1" });

        Assert.IsType<PermissionDecisionApproveOnce>(decision);
        Assert.Empty(published);
    }

    [Fact]
    public async Task Managed_request_still_requires_a_user_when_yolo_is_enabled()
    {
        var yolo = new YoloRegistry(NullLogger<YoloRegistry>.Instance);
        yolo.Set("session-1", enabled: true);
        var broker = CreateBroker(yolo);
        ApprovalRequired? approval = null;
        var handler = broker.CreateHandler(
            "session-1",
            (_, evt) => approval = Assert.IsType<ApprovalRequired>(evt),
            CancellationToken.None);

        var pending = handler(
            ShellRequest(managedApprovalRequired: true),
            new PermissionInvocation { SessionId = "session-1" });

        Assert.NotNull(approval);
        Assert.True(broker.Resolve(approval.ApprovalId, "allow_once"));
        Assert.IsType<PermissionDecisionApproveOnce>(await pending);
    }

    [Fact]
    public async Task Denial_resolves_the_existing_approval_surface()
    {
        var broker = CreateBroker(
            new YoloRegistry(NullLogger<YoloRegistry>.Instance));
        ApprovalRequired? approval = null;
        var handler = broker.CreateHandler(
            "session-1",
            (_, evt) => approval = Assert.IsType<ApprovalRequired>(evt),
            CancellationToken.None);

        var pending = handler(
            new PermissionRequestWrite
            {
                CanOfferSessionApproval = true,
                FileName = "README.md",
                Diff = "change",
                Intention = "Update documentation",
            },
            new PermissionInvocation { SessionId = "session-1" });

        Assert.NotNull(approval);
        Assert.Equal("Write README.md", approval.Title);
        Assert.True(broker.Resolve(approval.ApprovalId, "deny"));
        Assert.IsType<PermissionDecisionReject>(await pending);
    }

    [Fact]
    public async Task Session_cancellation_fails_pending_approval_closed()
    {
        var broker = CreateBroker(
            new YoloRegistry(NullLogger<YoloRegistry>.Instance));
        var handler = broker.CreateHandler(
            "session-1",
            (_, _) => { },
            CancellationToken.None);
        var pending = handler(
            new PermissionRequestUrl
            {
                Intention = "Fetch documentation",
                Url = "https://example.com",
            },
            new PermissionInvocation { SessionId = "session-1" });

        broker.CancelSession("session-1");

        Assert.IsType<PermissionDecisionUserNotAvailable>(await pending);
    }

    private static SdkPermissionBroker CreateBroker(YoloRegistry yolo) =>
        new(
            yolo,
            NullLogger<SdkPermissionBroker>.Instance,
            TimeSpan.FromSeconds(5));

    private static PermissionRequestShell ShellRequest(
        bool managedApprovalRequired = false) =>
        new()
        {
            CanOfferSessionApproval = true,
            Commands = [],
            FullCommandText = "git status",
            HasWriteFileRedirection = false,
            Intention = "Inspect repository status",
            ManagedApprovalRequired = managedApprovalRequired,
            PossiblePaths = [],
            PossibleUrls = [],
        };
}

#pragma warning restore GHCP001
