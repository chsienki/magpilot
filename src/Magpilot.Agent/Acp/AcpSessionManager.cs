using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Acp;

/// <summary>
/// Higher-level wrapper that bundles ACP method calls with structured event
/// dispatch. Holds a per-session subscriber list so HTTP SSE handlers can
/// fan out updates without re-parsing JSON.
///
/// Sessions are tagged with the <see cref="AcpFlavor"/> they were created
/// against; subsequent prompt/cancel/close calls for that session are routed
/// to the matching <see cref="AcpClient"/> instance from the pool.
/// </summary>
public sealed class AcpSessionManager
{
    private readonly AcpFlavorPool _pool;
    private readonly ILogger<AcpSessionManager> _logger;
    private readonly Dictionary<string, List<Channel<StreamEvent>>> _subscribers = new();
    private readonly object _subLock = new();
    private readonly Dictionary<string, TaskCompletionSource<ApprovalResponse>> _pendingApprovals = new();
    private readonly object _approvalLock = new();

    /// <summary>
    /// Maps sessionId -> the actual <see cref="AcpClient"/> that owns it.
    /// Multiplexing flavors share one client across sessions; non-multiplexing
    /// flavors (e.g. agency) get a dedicated client per session, also tracked
    /// here so we can clean up on close.
    /// </summary>
    private readonly ConcurrentDictionary<string, AcpClient> _sessionClient = new();

    /// <summary>
    /// Per-session "is a turn currently running?" tracking. Set when
    /// <see cref="PromptAsync"/> enters; cleared when it returns. Used by
    /// the GET /api/sessions/{id}/state endpoint to report activity, and
    /// by the magpilot launcher's acquire-for-host flow to politely wait for
    /// a turn boundary before taking ownership.
    /// </summary>
    private readonly ConcurrentDictionary<string, InFlightEntry> _inFlight = new();

    /// <summary>
    /// Per-session signal that fires every time a turn completes or
    /// errors. Used by <see cref="WaitForTurnBoundaryAsync"/> so callers
    /// (e.g. acquire-for-host) can block until the agent is idle without
    /// busy-polling.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _turnDone = new();

    public AcpSessionManager(AcpFlavorPool pool, ILogger<AcpSessionManager> logger)
    {
        _pool = pool;
        _logger = logger;
        _pool.OnSessionUpdate += HandleUpdate;
        _pool.OnRequest += HandleRequestAsync;
    }

    /// <summary>
    /// True if a turn is currently in flight on the agent's side for
    /// the given session.
    /// </summary>
    public bool IsTurnInFlight(string sessionId, out InFlightEntry entry)
        => _inFlight.TryGetValue(sessionId, out entry!);

    /// <summary>
    /// Wait until any in-flight turn for the session reaches a clean
    /// boundary (TurnComplete or error). Returns immediately if no turn
    /// is in flight. Honours <paramref name="ct"/> for the wait; on
    /// cancellation the in-flight turn is NOT aborted -- it'll keep
    /// running, but this caller stops waiting for it.
    /// </summary>
    public async Task WaitForTurnBoundaryAsync(string sessionId, CancellationToken ct)
    {
        if (!_inFlight.ContainsKey(sessionId)) return;
        var tcs = _turnDone.GetOrAdd(sessionId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    /// <summary>
    /// Resolve the ACP client owning <paramref name="sessionId"/>. If we
    /// don't know which client (e.g. session predates this agent process),
    /// fall back to the default-flavor client and remember the mapping.
    /// </summary>
    private async Task<AcpClient> ClientForAsync(string sessionId, CancellationToken ct)
    {
        if (_sessionClient.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }
        var fallback = await _pool.AcquireAsync(AcpFlavor.Default, ct);
        return _sessionClient.GetOrAdd(sessionId, fallback);
    }

    public async Task<string> NewSessionAsync(string cwd, AcpFlavor flavor, CancellationToken ct)
    {
        var client = await _pool.AcquireAsync(flavor, ct);
        var res = await client.CallAsync("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, ct);
        var sid = res?["sessionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("session/new returned no sessionId");
        _sessionClient[sid] = client;
        _logger.LogInformation("New ACP session {SessionId} cwd={Cwd} flavor={Flavor}", sid, cwd, flavor.Key);
        return sid;
    }

    public async Task LoadSessionAsync(string sessionId, string cwd, AcpFlavor flavor, CancellationToken ct)
    {
        var client = await _pool.AcquireAsync(flavor, ct);
        await client.CallAsync("session/load", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            ["mcpServers"] = new JsonArray(),
        }, ct, timeoutSec: 300);
        _sessionClient[sessionId] = client;
    }

    public async Task PromptAsync(string sessionId, string text, CancellationToken ct, string? requester = null)
    {
        _logger.LogDebug("PromptAsync sid={Sid} len={Len} requester={Requester}", sessionId, text.Length, requester ?? "(null)");
        var stopReason = "end_turn";
        _inFlight[sessionId] = new InFlightEntry(requester, DateTimeOffset.UtcNow);
        try
        {
            var client = await ClientForAsync(sessionId, ct);
            var resp = await client.CallAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = sessionId,
                ["prompt"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = text },
                },
            }, ct, timeoutSec: 600);
            stopReason = resp?["stopReason"]?.GetValue<string>() ?? stopReason;
        }
        catch (Exception ex)
        {
            stopReason = "error";
            _logger.LogWarning(ex, "session/prompt failed for {Sid}", sessionId);
        }
        finally
        {
            _inFlight.TryRemove(sessionId, out _);
            // Wake anyone waiting for the turn to finish.
            if (_turnDone.TryRemove(sessionId, out var tcs))
                tcs.TrySetResult();
        }

        // Notify subscribers so the SPA can clear its busy/thinking flags.
        Publish(sessionId, new TurnComplete(stopReason));
    }

    /// <summary>
    /// Push a synthesized event into the broadcast channel for a session.
    /// Used by sidecar code paths (e.g. quick-prompt with a pinned sessionId)
    /// to make a UserDelta visible to other connected subscribers (the SPA),
    /// since ACP doesn't echo the prompt text back during live prompts --
    /// it only emits user_message_chunk during session/load history replay.
    /// </summary>
    public void PublishToSubscribers(string sessionId, StreamEvent evt)
        => Publish(sessionId, evt);

    private void Publish(string sessionId, StreamEvent evt)
    {
        List<Channel<StreamEvent>>? list;
        lock (_subLock) _subscribers.TryGetValue(sessionId, out list);
        if (list is null) return;
        foreach (var ch in list)
            ch.Writer.TryWrite(evt);
    }

    public async Task CancelAsync(string sessionId, CancellationToken ct)
    {
        var client = await ClientForAsync(sessionId, ct);
        await client.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = sessionId });
        await Task.CompletedTask;
    }

    public async Task CloseAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var client = await ClientForAsync(sessionId, ct);
            await client.CallAsync("session/close", new JsonObject { ["sessionId"] = sessionId }, ct, timeoutSec: 30);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "session/close failed for {Sid}", sessionId); }
        finally
        {
            _sessionClient.TryRemove(sessionId, out _);
        }
    }

    public ChannelReader<StreamEvent> Subscribe(string sessionId)
    {
        var ch = Channel.CreateUnbounded<StreamEvent>(new UnboundedChannelOptions { SingleReader = true });
        int count;
        lock (_subLock)
        {
            if (!_subscribers.TryGetValue(sessionId, out var list))
                _subscribers[sessionId] = list = new();
            list.Add(ch);
            count = list.Count;
        }
        if (count > 1)
            _logger.LogWarning("Subscribe sid={Sid} -> {Count} subscribers (>1 means multiple SSE connections, expect duplicate UI events)", sessionId, count);
        else
            _logger.LogDebug("Subscribe sid={Sid} -> {Count}", sessionId, count);
        return ch.Reader;
    }

    public void Unsubscribe(string sessionId, ChannelReader<StreamEvent> reader)
    {
        int count = 0;
        lock (_subLock)
        {
            if (_subscribers.TryGetValue(sessionId, out var list))
            {
                list.RemoveAll(c => ReferenceEquals(c.Reader, reader));
                count = list.Count;
                if (list.Count == 0) _subscribers.Remove(sessionId);
            }
        }
        _logger.LogDebug("Unsubscribe sid={Sid} -> {Count}", sessionId, count);
    }

    public bool ResolveApproval(string approvalId, string optionId)
    {
        TaskCompletionSource<ApprovalResponse>? tcs;
        lock (_approvalLock) _pendingApprovals.Remove(approvalId, out tcs);
        return tcs?.TrySetResult(new ApprovalResponse(optionId)) ?? false;
    }

    private void HandleUpdate(string sessionId, JsonNode? update)
    {
        if (update is null) return;
        var kind = update["sessionUpdate"]?.GetValue<string>();
        _logger.LogDebug("HandleUpdate sid={Sid} kind={Kind}", sessionId, kind);
        StreamEvent? evt = kind switch
        {
            "agent_message_chunk" => new AssistantDelta(ExtractText(update["content"]) ?? ""),
            "agent_thought_chunk" => new ThoughtDelta(ExtractText(update["content"]) ?? ""),
            "user_message_chunk"  => new UserDelta(ExtractText(update["content"]) ?? ""),
            // ACP uses `tool_call` (status: pending) for new tool calls
            // and `tool_call_update` (status: in_progress | completed |
            // failed) for subsequent updates -- NOT the *_start / *_end
            // suffix variants. Map both to our existing StreamEvent
            // surface so SSE consumers (SPA, WhatsApp sidecar) see clean
            // ToolCallStart/End/Progress events at the right boundaries.
            "tool_call" => new ToolCallStart(
                update["toolCallId"]?.GetValue<string>() ?? "",
                update["title"]?.GetValue<string>() ?? update["kind"]?.GetValue<string>() ?? "tool",
                update["rawInput"]?.ToJsonString()),
            "tool_call_update" => MapToolCallUpdate(update),
            _ => null,
        };
        if (evt is null) return;

        List<Channel<StreamEvent>>? list;
        lock (_subLock) _subscribers.TryGetValue(sessionId, out list);
        if (list is null) return;
        foreach (var ch in list)
            ch.Writer.TryWrite(evt);
    }

    private static StreamEvent MapToolCallUpdate(JsonNode update)
    {
        var id = update["toolCallId"]?.GetValue<string>() ?? "";
        var status = update["status"]?.GetValue<string>();
        if (status is "completed" or "failed")
        {
            return new ToolCallEnd(
                id,
                update["rawOutput"]?.ToJsonString(),
                status == "completed");
        }
        return new ToolCallProgress(id, update["content"]?.ToJsonString());
    }

    private static string? ExtractText(JsonNode? content)
    {
        if (content is null) return null;
        if (content is JsonObject obj && obj["text"] is JsonNode t) return t.GetValue<string>();
        return content.ToString();
    }

    private async Task<JsonNode> HandleRequestAsync(string method, JsonNode? @params)
    {
        if (method != "session/request_permission")
            return new JsonObject();

        var sessionId = @params?["sessionId"]?.GetValue<string>() ?? "";
        var optsArr = @params?["options"] as JsonArray ?? new JsonArray();
        var options = new List<ApprovalOption>();
        foreach (var o in optsArr)
        {
            if (o is null) continue;
            options.Add(new ApprovalOption(
                o["optionId"]?.GetValue<string>() ?? "",
                o["name"]?.GetValue<string>() ?? o["optionId"]?.GetValue<string>() ?? "?",
                o["kind"]?.GetValue<string>()
            ));
        }

        // MAGPILOT_AUTO_APPROVE=true short-circuits the approval flow:
        // every session/request_permission picks an "allow"-flavored
        // option immediately. Intended for trusted always-on agents
        // like Magnus, where the trust boundary is the container itself
        // (everything inside is already authorized -- there's no human
        // sitting in front of a SPA to click "approve"). Without this
        // env var, /quick-prompt callers (WhatsApp sidecar, cron jobs)
        // can't drive any tool that requires permission (e.g. `edit`
        // on a file outside the session's cwd), because the approval
        // request fans out to SSE subscribers that have no UI to
        // answer, and times out after 5 minutes to a deny.
        if (string.Equals(
            Environment.GetEnvironmentVariable("MAGPILOT_AUTO_APPROVE"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            var pick = PickAllow(options);
            _logger.LogInformation(
                "Auto-approving permission request for session {Sid} -> {OptionId} (MAGPILOT_AUTO_APPROVE=true)",
                sessionId, pick);
            return BuildOutcome(pick, options);
        }

        var approvalId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ApprovalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_approvalLock) _pendingApprovals[approvalId] = tcs;

        var req = new ApprovalRequired(
            approvalId,
            @params?["toolCall"]?["title"]?.GetValue<string>() ?? "Permission required",
            @params?["toolCall"]?.ToJsonString(),
            options
        );

        List<Channel<StreamEvent>>? list;
        lock (_subLock) _subscribers.TryGetValue(sessionId, out list);
        if (list is not null)
            foreach (var ch in list) ch.Writer.TryWrite(req);

        // Wait up to 5 minutes for a client decision; default deny.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
            if (done != tcs.Task)
            {
                lock (_approvalLock) _pendingApprovals.Remove(approvalId);
                _logger.LogWarning("Approval {Id} timed out, denying", approvalId);
                return BuildOutcome("reject_once", options);
            }
            var resp = await tcs.Task;
            return BuildOutcome(resp.OptionId, options);
        }
        catch
        {
            lock (_approvalLock) _pendingApprovals.Remove(approvalId);
            return BuildOutcome("reject_once", options);
        }
    }

    /// <summary>
    /// Pick the most permissive "allow" option from the offered set.
    /// Prefers allow_always (so the model doesn't keep asking for the
    /// same kind of action), falls back to allow_once, then the first
    /// option that contains "allow" in its id, then the first option,
    /// then a literal "allow_once" string as a last resort.
    /// </summary>
    private static string PickAllow(IReadOnlyList<ApprovalOption> options)
    {
        var always = options.FirstOrDefault(o => o.OptionId == "allow_always");
        if (always is not null) return always.OptionId;
        var once = options.FirstOrDefault(o => o.OptionId == "allow_once");
        if (once is not null) return once.OptionId;
        var anyAllow = options.FirstOrDefault(o => o.OptionId.Contains("allow", StringComparison.OrdinalIgnoreCase));
        if (anyAllow is not null) return anyAllow.OptionId;
        return options.FirstOrDefault()?.OptionId ?? "allow_once";
    }

    private static JsonNode BuildOutcome(string optionId, IReadOnlyList<ApprovalOption> options)
    {
        var picked = optionId;
        if (!options.Any(o => o.OptionId == optionId))
            picked = options.FirstOrDefault()?.OptionId ?? "reject_once";
        return new JsonObject
        {
            ["outcome"] = new JsonObject
            {
                ["outcome"] = "selected",
                ["optionId"] = picked,
            }
        };
    }
}

/// <summary>
/// Snapshot of a single in-flight prompt/turn for a session, captured by
/// <see cref="AcpSessionManager.PromptAsync"/>. Surfaced via
/// <see cref="AcpSessionManager.IsTurnInFlight"/> for the GET /state endpoint.
/// </summary>
public readonly record struct InFlightEntry(string? Requester, DateTimeOffset StartedAt);
