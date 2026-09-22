#pragma warning disable GHCP001

using System.Collections.Concurrent;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Magpilot.Agent.Sessions;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Runtime.Sdk;

internal sealed class SdkPermissionBroker
{
    private static readonly IReadOnlyList<ApprovalOption> Options =
    [
        new("allow_once", "Allow once", "allow_once"),
        new("deny", "Deny", "reject"),
    ];

    private readonly YoloRegistry _yolo;
    private readonly ILogger<SdkPermissionBroker> _log;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();
    private readonly bool _environmentAutoApprove =
        string.Equals(
            Environment.GetEnvironmentVariable("MAGPILOT_AUTO_APPROVE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public SdkPermissionBroker(
        YoloRegistry yolo,
        ILogger<SdkPermissionBroker> log)
        : this(yolo, log, TimeSpan.FromMinutes(5))
    {
    }

    internal SdkPermissionBroker(
        YoloRegistry yolo,
        ILogger<SdkPermissionBroker> log,
        TimeSpan timeout)
    {
        _yolo = yolo;
        _log = log;
        _timeout = timeout;
        _yolo.Changed += OnYoloChanged;
    }

    public Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> CreateHandler(
        string sessionId,
        Action<string, StreamEvent> publish,
        CancellationToken sessionCt) =>
        (request, _) => HandleAsync(sessionId, request, publish, sessionCt);

    public bool Resolve(string approvalId, string optionId)
    {
        if (!_pending.TryRemove(approvalId, out var pending))
            return false;

        var decision = optionId switch
        {
            "allow_once" => PermissionDecision.ApproveOnce(),
            "deny" => PermissionDecision.Reject("The user denied this operation."),
            _ => PermissionDecision.Reject($"Unknown approval option '{optionId}'."),
        };
        return pending.Completion.TrySetResult(decision);
    }

    public void CancelSession(string sessionId)
    {
        foreach (var (approvalId, pending) in _pending)
        {
            if (!string.Equals(
                    pending.SessionId,
                    sessionId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (_pending.TryRemove(approvalId, out var removed))
                removed.Completion.TrySetResult(PermissionDecision.UserNotAvailable());
        }
    }

    private async Task<PermissionDecision> HandleAsync(
        string sessionId,
        PermissionRequest request,
        Action<string, StreamEvent> publish,
        CancellationToken sessionCt)
    {
        var autoApprove =
            request.ManagedApprovalRequired is not true &&
            (_environmentAutoApprove || _yolo.IsEnabled(sessionId));
        if (autoApprove)
        {
            _log.LogInformation(
                "Auto-approving SDK permission for session {SessionId} kind={Kind}",
                sessionId,
                request.Kind);
            return PermissionDecision.ApproveOnce();
        }

        var approvalId = Guid.NewGuid().ToString("N");
        var completion =
            new TaskCompletionSource<PermissionDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingApproval(
            sessionId,
            CanAutoApprove: request.ManagedApprovalRequired is not true,
            completion);
        if (!_pending.TryAdd(approvalId, pending))
            throw new InvalidOperationException("Could not register SDK approval request.");

        // The toggle can change between the first policy check and publishing
        // this pending request. Re-check after registration so that race cannot
        // strand an approval until timeout.
        if (pending.CanAutoApprove && _yolo.IsEnabled(sessionId))
        {
            ApprovePending(approvalId, pending);
        }

        var (title, detail) = Describe(request);
        if (!completion.Task.IsCompleted)
        {
            publish(
                sessionId,
                new ApprovalRequired(
                    approvalId,
                    title,
                    detail,
                    Options));
        }

        try
        {
            return await completion.Task.WaitAsync(_timeout, sessionCt);
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "SDK permission request {ApprovalId} timed out for session {SessionId}",
                approvalId,
                sessionId);
            return PermissionDecision.UserNotAvailable();
        }
        catch (OperationCanceledException) when (sessionCt.IsCancellationRequested)
        {
            return PermissionDecision.UserNotAvailable();
        }
        finally
        {
            _pending.TryRemove(approvalId, out _);
        }
    }

    private static (string Title, string? Detail) Describe(
        PermissionRequest request) =>
        request switch
        {
            PermissionRequestShell shell =>
                ("Run shell command", shell.FullCommandText),
            PermissionRequestWrite write =>
                ($"Write {write.FileName ?? "file"}", write.Diff),
            PermissionRequestRead read =>
                ($"Read {read.Path ?? "file"}", read.Path),
            PermissionRequestUrl url =>
                ("Access URL", url.Url),
            PermissionRequestMcp mcp =>
                (mcp.ToolTitle ?? mcp.ToolName ?? "Use MCP tool",
                    mcp.Args?.GetRawText()),
            PermissionRequestCustomTool custom =>
                (custom.ToolDescription ?? custom.ToolName ?? "Use custom tool",
                    custom.Args?.GetRawText()),
            _ => ($"Permission required: {request.Kind}", null),
        };

    private void OnYoloChanged(string sessionId, bool enabled)
    {
        if (!enabled)
            return;

        foreach (var (approvalId, pending) in _pending)
        {
            if (pending.CanAutoApprove &&
                string.Equals(
                    pending.SessionId,
                    sessionId,
                    StringComparison.Ordinal))
            {
                ApprovePending(approvalId, pending);
            }
        }
    }

    private void ApprovePending(
        string approvalId,
        PendingApproval pending)
    {
        if (!_pending.TryRemove(
                new KeyValuePair<string, PendingApproval>(
                    approvalId,
                    pending)))
        {
            return;
        }

        _log.LogInformation(
            "Auto-approving pending SDK permission {ApprovalId} after yolo was enabled for session {SessionId}",
            approvalId,
            pending.SessionId);
        pending.Completion.TrySetResult(PermissionDecision.ApproveOnce());
    }

    private sealed record PendingApproval(
        string SessionId,
        bool CanAutoApprove,
        TaskCompletionSource<PermissionDecision> Completion);
}

#pragma warning restore GHCP001
