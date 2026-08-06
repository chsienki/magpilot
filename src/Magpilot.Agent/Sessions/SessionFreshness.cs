using System.Collections.Concurrent;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Per-session freshness watermark: the size of a session's <c>events.jsonl</c>
/// the last time our ACP child synced it (loaded it, or completed a turn). If
/// the file has since grown, another process advanced the session past what our
/// in-memory child has seen -- the stale-resume condition. copilot won't re-read
/// disk for an already-loaded session, so this is how a resume decides whether
/// it needs to reload the session into a fresh child.
/// </summary>
public sealed class SessionFreshness
{
    private readonly ConcurrentDictionary<string, long> _served = new();

    /// <summary>Current on-disk watermark (events.jsonl byte length; 0 if absent/unreadable).</summary>
    public static long Watermark(string eventsPath)
    {
        try
        {
            var fi = new FileInfo(eventsPath);
            return fi.Exists ? fi.Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>Record that our child is now in sync with the on-disk state.</summary>
    public void RecordServed(string sessionId, string eventsPath) =>
        _served[sessionId] = Watermark(eventsPath);

    /// <summary>
    /// True if we have served this session before and its events file has grown
    /// since -- i.e. a foreign writer advanced it, so our copy may be behind.
    /// Returns false for a session we have never served (a first load reads disk).
    /// </summary>
    public bool MayBeStale(string sessionId, string eventsPath) =>
        _served.TryGetValue(sessionId, out var served) && IsStale(served, Watermark(eventsPath));

    /// <summary>Pure staleness comparison: disk has grown past what we last served.</summary>
    public static bool IsStale(long servedWatermark, long currentWatermark) =>
        currentWatermark > servedWatermark;

    public void Forget(string sessionId) => _served.TryRemove(sessionId, out _);
}
