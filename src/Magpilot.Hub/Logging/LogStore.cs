using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Magpilot.Hub.Logging;

/// <summary>
/// SQLite-backed central log store. One row per event from any Magpilot
/// component (SPA / hub / agents / sidecars). Indexed for cheap filtering
/// by source/level/timestamp; FTS5 over message+stack for substring search.
///
/// Retention: a background trim runs every 5 min, keeping the most recent
/// <see cref="MaxRows"/> rows (default 50,000) so the DB stays bounded
/// even on a chatty day.
/// </summary>
public sealed class LogStore : IHostedService, IDisposable
{
    private const int MaxRows = 50_000;
    private static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(5);

    private readonly string _dbPath;
    private readonly ILogger<LogStore> _logger;
    private CancellationTokenSource? _cts;
    private Task? _trimTask;

    public LogStore(IConfiguration config, ILogger<LogStore> logger)
    {
        _logger = logger;
        var dataDir = config["Hub:DataDir"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_HUB_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "logs.db");
        InitDb();
    }

    private string ConnString => $"Data Source={_dbPath};Pooling=True";

    private void InitDb()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS log_events (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                ts          INTEGER NOT NULL,           -- unix ms
                source      TEXT    NOT NULL,
                level       TEXT    NOT NULL,
                category    TEXT,
                message     TEXT    NOT NULL,
                stack       TEXT,
                session_id  TEXT,
                extra       TEXT,
                user_agent  TEXT,
                url         TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_log_ts          ON log_events(ts DESC);
            CREATE INDEX IF NOT EXISTS idx_log_level       ON log_events(level);
            CREATE INDEX IF NOT EXISTS idx_log_source      ON log_events(source);
            CREATE INDEX IF NOT EXISTS idx_log_session_id  ON log_events(session_id);

            CREATE VIRTUAL TABLE IF NOT EXISTS log_events_fts USING fts5(
                message, stack, content='log_events', content_rowid='id'
            );
            CREATE TRIGGER IF NOT EXISTS log_events_ai AFTER INSERT ON log_events BEGIN
                INSERT INTO log_events_fts(rowid, message, stack)
                VALUES (new.id, new.message, COALESCE(new.stack, ''));
            END;
            CREATE TRIGGER IF NOT EXISTS log_events_ad AFTER DELETE ON log_events BEGIN
                INSERT INTO log_events_fts(log_events_fts, rowid, message, stack)
                VALUES ('delete', old.id, old.message, COALESCE(old.stack, ''));
            END;
            """;
        cmd.ExecuteNonQuery();
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _trimTask = Task.Run(() => TrimLoop(_cts.Token));
        _logger.LogInformation("LogStore ready at {Path} (max {Max} rows)", _dbPath, MaxRows);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_trimTask is not null)
        {
            try { await _trimTask.WaitAsync(ct); }
            catch { /* shutting down */ }
        }
    }

    public void Dispose() => _cts?.Dispose();

    private async Task TrimLoop(CancellationToken ct)
    {
        try
        {
            using var t = new PeriodicTimer(TrimInterval);
            while (await t.WaitForNextTickAsync(ct))
            {
                try { Trim(); }
                catch (Exception ex) { _logger.LogWarning(ex, "log trim failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Bulk-insert; one transaction so writers don't fight WAL.</summary>
    public void Append(IEnumerable<LogEventDto> batch)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO log_events (ts, source, level, category, message, stack, session_id, extra, user_agent, url)
            VALUES ($ts, $source, $level, $category, $message, $stack, $session_id, $extra, $user_agent, $url)
            """;
        var ts       = cmd.CreateParameter(); ts.ParameterName = "$ts";
        var source   = cmd.CreateParameter(); source.ParameterName = "$source";
        var level    = cmd.CreateParameter(); level.ParameterName = "$level";
        var category = cmd.CreateParameter(); category.ParameterName = "$category";
        var message  = cmd.CreateParameter(); message.ParameterName = "$message";
        var stack    = cmd.CreateParameter(); stack.ParameterName = "$stack";
        var sid      = cmd.CreateParameter(); sid.ParameterName = "$session_id";
        var extra    = cmd.CreateParameter(); extra.ParameterName = "$extra";
        var ua       = cmd.CreateParameter(); ua.ParameterName = "$user_agent";
        var url      = cmd.CreateParameter(); url.ParameterName = "$url";
        cmd.Parameters.AddRange(new[] { ts, source, level, category, message, stack, sid, extra, ua, url });

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var e in batch)
        {
            ts.Value       = nowMs;
            source.Value   = Truncate(e.Source, 64);
            level.Value    = NormalizeLevel(e.Level);
            category.Value = (object?)Truncate(e.Category, 256) ?? DBNull.Value;
            message.Value  = Truncate(e.Message, 8192);
            stack.Value    = (object?)Truncate(e.Stack, 16384) ?? DBNull.Value;
            sid.Value      = (object?)Truncate(e.SessionId, 64) ?? DBNull.Value;
            extra.Value    = e.Extra is { } ext ? ext.GetRawText() : (object)DBNull.Value;
            ua.Value       = (object?)Truncate(e.UserAgent, 512) ?? DBNull.Value;
            url.Value      = (object?)Truncate(e.Url, 2048) ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<LogEventRow> Query(LogQuery q)
    {
        var limit = Math.Clamp(q.Limit ?? 200, 1, 2000);
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();

        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT id, ts, source, level, category, message, stack, session_id, extra, user_agent, url ");
        sb.Append("FROM log_events WHERE 1=1 ");
        if (!string.IsNullOrWhiteSpace(q.Source))
        {
            sb.Append("AND source = $source "); cmd.Parameters.AddWithValue("$source", q.Source);
        }
        if (!string.IsNullOrWhiteSpace(q.Level))
        {
            sb.Append("AND level = $level "); cmd.Parameters.AddWithValue("$level", NormalizeLevel(q.Level));
        }
        if (!string.IsNullOrWhiteSpace(q.SessionId))
        {
            sb.Append("AND session_id = $sid "); cmd.Parameters.AddWithValue("$sid", q.SessionId);
        }
        if (q.Since is { } since)
        {
            sb.Append("AND ts >= $since "); cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            sb.Append("AND id IN (SELECT rowid FROM log_events_fts WHERE log_events_fts MATCH $q) ");
            cmd.Parameters.AddWithValue("$q", q.Search);
        }
        sb.Append("ORDER BY id DESC LIMIT $limit");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = sb.ToString();

        var list = new List<LogEventRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LogEventRow(
                Id:         r.GetInt64(0),
                Timestamp:  DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),
                Source:     r.GetString(2),
                Level:      r.GetString(3),
                Category:   r.IsDBNull(4) ? null : r.GetString(4),
                Message:    r.GetString(5),
                Stack:      r.IsDBNull(6) ? null : r.GetString(6),
                SessionId:  r.IsDBNull(7) ? null : r.GetString(7),
                Extra:      r.IsDBNull(8) ? null : r.GetString(8),
                UserAgent:  r.IsDBNull(9) ? null : r.GetString(9),
                Url:        r.IsDBNull(10) ? null : r.GetString(10)));
        }
        return list;
    }

    public IReadOnlyList<string> KnownSources()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT source FROM log_events ORDER BY source";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private void Trim()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM log_events
            WHERE id IN (
                SELECT id FROM log_events ORDER BY id DESC LIMIT -1 OFFSET $keep
            );
            """;
        cmd.Parameters.AddWithValue("$keep", MaxRows);
        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0) _logger.LogDebug("trimmed {N} old log rows", deleted);
    }

    private static string NormalizeLevel(string level) => level switch
    {
        "Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical" => level,
        _ when level.Equals("info", StringComparison.OrdinalIgnoreCase) => "Information",
        _ when level.Equals("warn", StringComparison.OrdinalIgnoreCase) => "Warning",
        _ when level.Equals("err", StringComparison.OrdinalIgnoreCase) => "Error",
        _ when level.Equals("fatal", StringComparison.OrdinalIgnoreCase) => "Critical",
        _ => level.Length > 16 ? level[..16] : level,
    };

    private static string? Truncate(string? s, int max)
    {
        if (s is null) return null;
        return s.Length <= max ? s : s[..max];
    }
}
