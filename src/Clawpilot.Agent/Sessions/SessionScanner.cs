using System.Diagnostics;
using System.Globalization;
using Clawpilot.Shared.Models;

namespace Clawpilot.Agent.Sessions;

/// <summary>
/// Enumerates Copilot CLI's on-disk session-state directory.
/// Each subdirectory is a session UUID. Presence of <c>inuse.&lt;PID&gt;.lock</c>
/// indicates a live session; the PID is parsed from the filename.
/// </summary>
public sealed class SessionScanner
{
    private readonly ILogger<SessionScanner> _logger;
    private readonly string _root;

    public SessionScanner(ILogger<SessionScanner> logger, string? rootOverride = null)
    {
        _logger = logger;
        _root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");
    }

    public string Root => _root;

    public IEnumerable<SessionInfo> Enumerate(IReadOnlySet<string> ownedSessionIds)
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            SessionInfo? info;
            try { info = Parse(dir, ownedSessionIds); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to parse session dir {Dir}", dir); continue; }
            if (info is not null) yield return info;
        }
    }

    public SessionInfo? Get(string sessionId, IReadOnlySet<string> ownedSessionIds)
    {
        var dir = Path.Combine(_root, sessionId);
        return Directory.Exists(dir) ? Parse(dir, ownedSessionIds) : null;
    }

    private static SessionInfo Parse(string dir, IReadOnlySet<string> owned)
    {
        var id = Path.GetFileName(dir);
        var lockFile = Directory.EnumerateFiles(dir, "inuse.*.lock").FirstOrDefault();
        int? ownerPid = null;
        if (lockFile is not null)
        {
            var name = Path.GetFileName(lockFile);
            var parts = name.Split('.');
            if (parts.Length >= 3 && int.TryParse(parts[1], out var pid))
                ownerPid = pid;
        }

        var state = lockFile is null
            ? SessionState.Past
            : (owned.Contains(id) ? SessionState.LiveOwned : SessionState.LiveOrphan);

        // Sanity: if PID is recorded but the process no longer exists, treat as Past.
        if (state != SessionState.Past && ownerPid is int p)
        {
            try { _ = Process.GetProcessById(p); }
            catch { state = SessionState.Past; }
        }

        var (cwd, repository, branch, summary, createdAt, updatedAt) = ParseWorkspaceYaml(Path.Combine(dir, "workspace.yaml"));
        // Prefer the most recent activity signal: events.jsonl mtime > workspace.yaml mtime > dir mtime.
        // workspace.yaml's updated_at field isn't rewritten on every message,
        // so it's a poor sort key on its own.
        var derivedUpdated = LatestMTime(dir, ["events.jsonl", "workspace.yaml"]);
        if (derivedUpdated is { } d && (updatedAt is null || d > updatedAt))
            updatedAt = d;
        return new SessionInfo(id, state, cwd, repository, branch, summary, ownerPid, createdAt, updatedAt);
    }

    private static DateTimeOffset? LatestMTime(string dir, string[] candidates)
    {
        DateTimeOffset? best = null;
        foreach (var name in candidates)
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            var t = new DateTimeOffset(File.GetLastWriteTimeUtc(p), TimeSpan.Zero);
            if (best is null || t > best) best = t;
        }
        if (best is null && Directory.Exists(dir))
            best = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
        return best;
    }

    /// <summary>
    /// Tiny line-based YAML reader. Only handles the flat top-level keys
    /// the Copilot CLI actually writes to workspace.yaml. No nested maps,
    /// no anchors, no folded scalars.
    /// </summary>
    private static (string? cwd, string? repo, string? branch, string? summary, DateTimeOffset? created, DateTimeOffset? updated)
        ParseWorkspaceYaml(string path)
    {
        if (!File.Exists(path)) return (null, null, null, null, null, null);
        string? cwd = null, repo = null, branch = null, summary = null;
        DateTimeOffset? created = null, updated = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            var idx = line.IndexOf(':');
            if (idx <= 0 || line.StartsWith(' ') || line.StartsWith('-')) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim().Trim('"', '\'');
            switch (key)
            {
                case "cwd": cwd = val; break;
                case "repository": repo = val; break;
                case "branch": branch = val; break;
                case "summary": summary = val; break;
                case "created_at":
                    if (DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c))
                        created = c;
                    break;
                case "updated_at":
                    if (DateTimeOffset.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u))
                        updated = u;
                    break;
            }
        }
        return (cwd, repo, branch, summary, created, updated);
    }
}
