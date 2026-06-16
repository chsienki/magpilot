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
    private readonly Magpilot.Agent.Sessions.YoloRegistry _yolo;
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

    public AcpSessionManager(AcpFlavorPool pool, Magpilot.Agent.Sessions.YoloRegistry yolo, ILogger<AcpSessionManager> logger)
    {
        _pool = pool;
        _yolo = yolo;
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

    /// <summary>
    /// Detach a session from this agent's ACP child. Calls
    /// <c>session/close</c> over JSON-RPC (which copilot --acp
    /// actually rejects with -32601 today, but we issue it anyway in
    /// case a future copilot adds support) and then sweeps the
    /// session directory to remove the agent's
    /// <c>inuse.&lt;acp-pid&gt;.lock</c> file. The lock removal is
    /// the load-bearing step for cooperative handoff: even though
    /// the ACP child still has the session in memory, the on-disk
    /// lock is what other copilot processes (a launcher's
    /// interactive child, terminal-driven <c>copilot --resume</c>,
    /// etc.) consult to decide whether the session is "in use".
    /// Without this cleanup, every launcher startup against a
    /// session the agent loaded prints a "session is locked by
    /// another process" warning and the new copilot child appends
    /// its own lock alongside (multi-lock advisory state).
    /// </summary>
    /// <param name="sessionId">The session being detached.</param>
    /// <param name="sessionsRoot">
    /// The Copilot CLI's session-state root directory (typically
    /// <c>~/.copilot/session-state</c>). Pass null to skip the lock
    /// cleanup -- only meaningful for the in-process unit tests.
    /// </param>
    public async Task CloseAsync(string sessionId, string? sessionsRoot, CancellationToken ct)
    {
        // Capture the client's PID BEFORE the CloseAsync call, since
        // we need it to identify which lock file to delete and the
        // call might null-out our cache mapping on success.
        AcpClient? client = null;
        _sessionClient.TryGetValue(sessionId, out client);
        var clientPid = client?.ProcessId;

        try
        {
            if (client is not null)
                await client.CallAsync("session/close", new JsonObject { ["sessionId"] = sessionId }, ct, timeoutSec: 30);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "session/close failed for {Sid}", sessionId); }
        finally
        {
            if (sessionsRoot is not null && clientPid is int pid)
            {
                try
                {
                    var lockFile = Path.Combine(sessionsRoot, sessionId, $"inuse.{pid}.lock");
                    if (File.Exists(lockFile))
                    {
                        File.Delete(lockFile);
                        _logger.LogInformation("Removed lock {File} after detach (pid={Pid})", lockFile, pid);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove lock for session {Sid}", sessionId);
                }
            }
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
        if (evt is null)
        {
            // Unknown sessionUpdate kind -- surface at Warning so a new
            // ACP addition that we'd otherwise silently drop shows up in
            // /admin/logs (instead of waiting for a user-visible symptom
            // like "the SPA stopped reflecting some new event type").
            // Kinds we knowingly ignore (available_commands_update,
            // config_option_update, plan, current_mode_update) are common
            // enough to be noisy -- whitelist them. Anything else is news.
            if (kind is not null
                && kind is not "available_commands_update"
                && kind is not "config_option_update"
                && kind is not "plan"
                && kind is not "current_mode_update")
            {
                var raw = update.ToJsonString();
                if (raw.Length > 400) raw = raw[..400] + "...";
                _logger.LogWarning(
                    "HandleUpdate unknown sessionUpdate kind={Kind} sid={Sid} raw={Raw}",
                    kind, sessionId, raw);
            }
            return;
        }

        // Diagnostic: when an agent_message_chunk delivers text that
        // looks like an inline "Info: <path>" tool-notice (drive letter
        // or unix root immediately after the prefix), log the chunk +
        // the surrounding raw update at Warning. Copilot CLI shouldn't
        // be folding those into the agent message stream, but if it
        // does we want to see the wire shape so we can route them to
        // a tool chip instead of letting them bleed into the assistant
        // bubble. Heuristic only; remove once root cause is known.
        if (evt is AssistantDelta ad
            && ad.Text.Length > 7
            && ad.Text.StartsWith("Info: ", StringComparison.Ordinal)
            && IsInfoPathBleed(ad.Text))
        {
            var raw = update.ToJsonString();
            if (raw.Length > 600) raw = raw[..600] + "...";
            _logger.LogWarning(
                "HandleUpdate Info: bleed in agent_message_chunk sid={Sid} text={Text} raw={Raw}",
                sessionId,
                ad.Text.Length > 200 ? ad.Text[..200] + "..." : ad.Text,
                raw);
        }

        List<Channel<StreamEvent>>? list;
        lock (_subLock) _subscribers.TryGetValue(sessionId, out list);
        if (list is null) return;
        foreach (var ch in list)
            ch.Writer.TryWrite(evt);
    }

    private static bool IsInfoPathBleed(string text)
    {
        // "Info: <drive-letter>:\..." (Windows) or "Info: /..." (Unix).
        // Don't fire on plain prose that happens to start with "Info: ".
        if (text.Length < 8) return false;
        var rest = text.AsSpan(6); // skip "Info: "
        if (rest.Length >= 3 && char.IsLetter(rest[0]) && rest[1] == ':' && (rest[2] == '\\' || rest[2] == '/')) return true;
        if (rest.Length >= 1 && rest[0] == '/') return true;
        return false;
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

        // Auto-approve fast path. Two independent ways to trigger,
        // each with its own ergonomics:
        //
        //   * Per-session yolo flag (YoloRegistry) -- set by the SPA's
        //     per-session toggle for a session the user explicitly
        //     opted in. Picks `allow_once` so flipping yolo OFF restores
        //     manual approval immediately for any new permission
        //     request. `allow_always` would persist the decision in the
        //     CLI's per-session policy memory, so previously-touched
        //     paths/tools would silently stay allowed even after yolo
        //     is turned off -- surprising and incorrect for an
        //     opt-in/opt-out toggle.
        //
        //   * MAGPILOT_AUTO_APPROVE=true env var -- legacy host-wide
        //     fallback for always-on container agents like Magnus
        //     where there's no human at a SPA to click "approve" and
        //     re-prompting on every call would slow the agent down.
        //     Picks `allow_always` so a long-running session caches
        //     decisions (the same behaviour this shortcut has always
        //     had).
        //
        // Either source short-circuits the SSE approval round-trip
        // the same way. The host-level MAGPILOT_YOLO_DISABLED guard is
        // enforced inside YoloRegistry (IsEnabled is always false), so
        // we never need to check it here.
        var perSessionYolo = _yolo.IsEnabled(sessionId);
        var envWideAutoApprove = string.Equals(
            Environment.GetEnvironmentVariable("MAGPILOT_AUTO_APPROVE"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (perSessionYolo || envWideAutoApprove)
        {
            var pick = perSessionYolo ? PickAllow(options, sticky: false) : PickAllow(options, sticky: true);
            var source = perSessionYolo ? "yolo" : "MAGPILOT_AUTO_APPROVE";
            _logger.LogInformation(
                "Auto-approving permission request for session {Sid} -> {OptionId} ({Source})",
                sessionId, pick, source);
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
    /// <summary>
    /// Pick an "allow"-flavored option from the ACP permission
    /// request's option list.
    /// </summary>
    /// <param name="sticky">
    /// True: prefer <c>allow_always</c> so the Copilot CLI caches the
    /// decision for the rest of the session and stops re-asking.
    /// Right for unattended sidecars (env-wide MAGPILOT_AUTO_APPROVE).
    /// False: prefer <c>allow_once</c> so each request stays individually
    /// approved. Right for the user-facing per-session yolo toggle,
    /// where flipping yolo off should immediately restore manual
    /// approval for any new permission request -- including for tools
    /// or paths the agent already touched while yolo was on.
    /// </param>
    private static string PickAllow(IReadOnlyList<ApprovalOption> options, bool sticky)
    {
        if (sticky)
        {
            var always = options.FirstOrDefault(o => o.OptionId == "allow_always");
            if (always is not null) return always.OptionId;
            var fallbackOnce = options.FirstOrDefault(o => o.OptionId == "allow_once");
            if (fallbackOnce is not null) return fallbackOnce.OptionId;
        }
        else
        {
            var once = options.FirstOrDefault(o => o.OptionId == "allow_once");
            if (once is not null) return once.OptionId;
            // No allow_once exposed? Fall back to allow_always so we
            // don't block the turn; the comment on the call site
            // documents the tradeoff.
            var fallbackAlways = options.FirstOrDefault(o => o.OptionId == "allow_always");
            if (fallbackAlways is not null) return fallbackAlways.OptionId;
        }
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
