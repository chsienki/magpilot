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
///
/// Paging: long-lived pinned sessions (Magnus) can grow into thousands of
/// messages; sending the entire projection on every fresh tab open is
/// painful on mobile. <see cref="ReadTail"/> returns the most recent N
/// entries and <see cref="ReadBefore"/> returns the N entries immediately
/// older than a given ordinal cursor, so the SPA can demand-load as the
/// user scrolls up.
///
/// Cursor model: each projected <see cref="HistoryEntry"/> gets a stable
/// ordinal <see cref="HistoryEntry.Id"/> -- its position in the
/// canonical projection of events.jsonl. The file is append-only, so the
/// projection is deterministic across calls. <see cref="HistoryPage.OldestCursor"/>
/// is the ordinal of the first entry returned, suitable for passing back
/// as the <c>before</c> param on the next paging call.
///
/// Performance: every call re-streams events.jsonl. For O(thousands of
/// entries) this is fine. If we ever hit O(100k+) sessions, the next
/// step is an in-memory LRU cache of recent projections keyed by
/// (sessionId, fileLastModified). Not built yet -- profile first.
/// </summary>
public sealed class HistoryReader
{
    private readonly ILogger<HistoryReader> _log;

    public HistoryReader(ILogger<HistoryReader> log) => _log = log;

    /// <summary>One row in the projected history. Mirrors the SPA's ChatMessage shape.</summary>
    /// <param name="Id">Stable ordinal cursor: position in the canonical projection of events.jsonl. Pass back as <c>before</c> on the next paging call.</param>
    /// <param name="ToolStatus">For <c>Role == "tool"</c> rows, the lifecycle state. Always <see cref="HistoryToolStatus.Pending"/> for non-tool rows; updated to Ok or Fail when the matching <c>tool.execution_complete</c> event is encountered during projection.</param>
    public sealed record HistoryEntry(
        int Id,
        string Role,
        string Text,
        string? ToolCallId = null,
        HistoryToolStatus ToolStatus = HistoryToolStatus.Pending);

    /// <summary>
    /// Lifecycle state of a tool call as projected from events.jsonl.
    /// Wire-format mirror of the SPA's <c>Magpilot.UI.Components.ToolStatus</c>.
    /// </summary>
    public enum HistoryToolStatus
    {
        Pending,
        Ok,
        Fail,
    }

    /// <summary>A page of history with cursor + has-more flag.</summary>
    /// <param name="Entries">The page, oldest-first.</param>
    /// <param name="OldestCursor">Ordinal of the first entry returned. Use as <c>before</c> on the next call.</param>
    /// <param name="HasMore">True if there are older entries before <see cref="OldestCursor"/>.</param>
    public sealed record HistoryPage(
        IReadOnlyList<HistoryEntry> Entries,
        int OldestCursor,
        bool HasMore);

    /// <summary>
    /// Return the most recent <paramref name="limit"/> entries for the
    /// session. If the session has fewer entries, returns all of them and
    /// sets <see cref="HistoryPage.HasMore"/> = false.
    /// </summary>
    public HistoryPage ReadTail(string sessionId, int limit)
    {
        if (limit <= 0) limit = 50;
        var all = ProjectAll(sessionId);
        var start = Math.Max(0, all.Count - limit);
        var entries = all.GetRange(start, all.Count - start);
        return new HistoryPage(entries, start, start > 0);
    }

    /// <summary>
    /// Return the <paramref name="limit"/> entries immediately preceding
    /// the <paramref name="before"/> ordinal cursor (i.e. older than it,
    /// not including it). Pass the previous call's
    /// <see cref="HistoryPage.OldestCursor"/> as <paramref name="before"/>.
    /// </summary>
    public HistoryPage ReadBefore(string sessionId, int before, int limit)
    {
        if (limit <= 0) limit = 50;
        if (before <= 0) return new HistoryPage([], 0, false);
        var all = ProjectAll(sessionId);
        var end = Math.Min(before, all.Count);
        var start = Math.Max(0, end - limit);
        var entries = all.GetRange(start, end - start);
        return new HistoryPage(entries, start, start > 0);
    }

    /// <summary>
    /// Legacy: return the full projection. Kept for callers that
    /// explicitly want everything (rare). Prefer <see cref="ReadTail"/>
    /// for first-paint and <see cref="ReadBefore"/> for paging.
    /// </summary>
    public IReadOnlyList<HistoryEntry> Read(string sessionId) => ProjectAll(sessionId);

    private List<HistoryEntry> ProjectAll(string sessionId)
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
        // tool.execution_complete arrives AFTER the assistant.message
        // that announced the tool request. We project one entry per
        // tool call (no separate [start] / [end] pair), then mutate
        // its status in place when the matching complete event lands.
        // This map keys by ACP toolCallId -> index into rows[].
        var toolIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
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
                            rows.Add(new HistoryEntry(rows.Count, "user", content));
                        break;
                    }
                case "assistant.message":
                    {
                        // The model's reasoning stream (visible only when
                        // "Show thinking" is on) precedes the spoken reply.
                        var reasoning = data["reasoningText"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(reasoning))
                            rows.Add(new HistoryEntry(rows.Count, "thought", reasoning));

                        var content = data["content"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(content))
                            rows.Add(new HistoryEntry(rows.Count, "assistant", content));

                        // toolRequests: the model called one or more tools
                        // before this message. Surface each as a single
                        // pending chip; tool.execution_complete (below)
                        // mutates the chip's ToolStatus in place.
                        if (data["toolRequests"] is JsonArray reqs)
                        {
                            foreach (var r in reqs)
                            {
                                if (r is null) continue;
                                var toolCallId = r["toolCallId"]?.GetValue<string>();
                                var toolName = r["name"]?.GetValue<string>() ?? "tool";
                                // intentionSummary is a human-readable
                                // sentence the CLI synthesizes from the
                                // tool args, e.g. "edit the file at
                                // D:\..\PlaybackService.kt." It's closer
                                // to what the live wire's `title` shows
                                // than the bare tool name, so prefer it
                                // for the chip label when available.
                                var label = r["intentionSummary"]?.GetValue<string>();
                                if (string.IsNullOrWhiteSpace(label)) label = toolName;
                                var idx = rows.Count;
                                rows.Add(new HistoryEntry(idx, "tool", label, toolCallId, HistoryToolStatus.Pending));
                                if (!string.IsNullOrEmpty(toolCallId))
                                    toolIndexById[toolCallId] = idx;
                            }
                        }
                        break;
                    }
                case "tool.execution_complete":
                    {
                        var toolCallId = data["toolCallId"]?.GetValue<string>();
                        var success = data["success"]?.GetValue<bool>() ?? false;
                        var status = success ? HistoryToolStatus.Ok : HistoryToolStatus.Fail;
                        if (!string.IsNullOrEmpty(toolCallId)
                            && toolIndexById.TryGetValue(toolCallId, out var idx))
                        {
                            // Mutate the existing pending entry. Don't
                            // add a new row -- that'd give us the old
                            // two-row [start]/[end] shape we just got
                            // rid of.
                            rows[idx] = rows[idx] with { ToolStatus = status };
                        }
                        else
                        {
                            // Orphan complete with no matching tool
                            // request announcement (shouldn't happen in
                            // a well-formed log but defend against it).
                            rows.Add(new HistoryEntry(rows.Count, "tool", "(orphaned tool result)", toolCallId, status));
                        }
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
