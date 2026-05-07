using System.Text.Json;
using System.Text.Json.Nodes;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Reads a session's <c>events.jsonl</c> directly from disk and projects it
/// into a flat list of role/text rows suitable for the SPA's chat history
/// rehydration.
///
/// Why this exists: ACP's <c>session/load</c> is the only way to make a
/// dormant session live, AND it streams the full history back as
/// user_message_chunk + agent_message_chunk events. But ACP rejects
/// double-load on an already-loaded session (-32602), so when an Owned
/// session has been touched by another client (e.g. the WhatsApp sidecar)
/// the SPA can't request load=true to retrieve history. Reading the
/// canonical events.jsonl bypasses ACP entirely; the file is the durable
/// source of truth that ACP itself was going to replay anyway.
/// </summary>
public sealed class HistoryReader
{
    private readonly ILogger<HistoryReader> _log;

    public HistoryReader(ILogger<HistoryReader> log) => _log = log;

    /// <summary>One row in the projected history. Mirrors the SPA's ChatMessage shape.</summary>
    public sealed record HistoryEntry(string Role, string Text, string? ToolCallId = null);

    /// <summary>
    /// Read and project the events for the given session id. Returns an
    /// empty list if the session dir or events.jsonl doesn't exist (e.g.
    /// a session that was created but never used).
    /// </summary>
    public IReadOnlyList<HistoryEntry> Read(string sessionId)
    {
        var sessionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state", sessionId);
        var path = Path.Combine(sessionRoot, "events.jsonl");

        if (!File.Exists(path))
        {
            _log.LogDebug("HistoryReader: no events.jsonl at {Path}", path);
            return [];
        }

        var rows = new List<HistoryEntry>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? evt;
            try { evt = JsonNode.Parse(line); }
            catch (JsonException ex)
            {
                _log.LogDebug(ex, "Skipping malformed events.jsonl line in {Sid}", sessionId);
                continue;
            }
            if (evt is null) continue;

            var type = evt["type"]?.GetValue<string>();
            var data = evt["data"];
            if (type is null || data is null) continue;

            switch (type)
            {
                case "user.message":
                    {
                        // Prefer the user's verbatim content over the
                        // server-transformed version (which prepends a
                        // synthesized current_datetime block).
                        var content = data["content"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(content))
                            rows.Add(new HistoryEntry("user", content));
                        break;
                    }
                case "assistant.message":
                    {
                        // The model's reasoning stream (visible only when
                        // "Show thinking" is on) precedes the spoken reply.
                        var reasoning = data["reasoningText"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(reasoning))
                            rows.Add(new HistoryEntry("thought", reasoning));

                        var content = data["content"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(content))
                            rows.Add(new HistoryEntry("assistant", content));

                        // toolRequests: the model called one or more tools
                        // before this message. We surface them as compact
                        // chips to mirror what the live stream would show.
                        if (data["toolRequests"] is JsonArray reqs)
                        {
                            foreach (var r in reqs)
                            {
                                if (r is null) continue;
                                var toolCallId = r["toolCallId"]?.GetValue<string>();
                                var toolName = r["name"]?.GetValue<string>() ?? "tool";
                                rows.Add(new HistoryEntry("tool", $"[start] {toolName}", toolCallId));
                            }
                        }
                        break;
                    }
                case "tool.execution_complete":
                    {
                        var toolCallId = data["toolCallId"]?.GetValue<string>();
                        var success = data["success"]?.GetValue<bool>() ?? false;
                        rows.Add(new HistoryEntry("tool", $"[end] {(success ? "ok" : "fail")}", toolCallId));
                        break;
                    }
                // session.start/resume/model_change, system.message,
                // assistant.turn_*, hook.*, tool.execution_start
                // are intentionally ignored for the chat surface; they're
                // either bookkeeping or already covered by the
                // assistant.message envelope above.
            }
        }
        return rows;
    }
}
