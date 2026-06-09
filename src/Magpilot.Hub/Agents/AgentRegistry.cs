using Microsoft.Data.Sqlite;
using Magpilot.Shared.Models;

namespace Magpilot.Hub.Agents;

/// <summary>
/// SQLite-backed agent address book. Stores known per-host agents so they
/// survive hub restart even before the next UDP discovery sweep.
/// </summary>
public sealed class AgentRegistry
{
    private readonly string _dbPath;
    private readonly ILogger<AgentRegistry> _logger;
    private readonly Dictionary<string, AgentInfo> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TimeSpan _staleAfter;

    public AgentRegistry(IConfiguration config, ILogger<AgentRegistry> logger)
    {
        _logger = logger;
        var dataDir = config["Hub:DataDir"]
            ?? Environment.GetEnvironmentVariable("MAGPILOT_HUB_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "hub.db");
        // Default: ~3x the discovery interval (60s) gives slack for one missed sweep.
        var staleSec = config.GetValue("Hub:AgentStaleSec", 180);
        _staleAfter = TimeSpan.FromSeconds(staleSec);
        InitDb();
        Load();
    }

    private string ConnString => $"Data Source={_dbPath}";

    /// <summary>
    /// Connection string for callers that need to share the database
    /// (e.g. <c>EnrollmentService</c> for the V2a voucher tables). The
    /// hub deliberately co-locates all its SQLite state in one file so
    /// transactional integrity across e.g. voucher-redeem +
    /// agent-upsert is trivial.
    /// </summary>
    internal string ConnStringInternal => ConnString;

    private void InitDb()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agents (
                name TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                token TEXT,
                last_seen INTEGER
            );
            CREATE TABLE IF NOT EXISTS subscriptions (
                id TEXT PRIMARY KEY,
                identity TEXT NOT NULL,
                kind TEXT NOT NULL,
                endpoint TEXT NOT NULL,
                keys_json TEXT,
                user_agent TEXT,
                last_seen INTEGER
            );
            -- V2a pairing (magpilot-pairing.md): one-time enrollment vouchers.
            -- Vouchers are short-lived single-use secrets. The hub stores
            -- only the SHA256 hash so a database leak doesn't immediately
            -- give an attacker reusable credentials. The created_by_user
            -- column is the github login from the issuing cookie; useful
            -- audit trail when revocation lands in V2b.
            CREATE TABLE IF NOT EXISTS vouchers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                secret_hash BLOB NOT NULL UNIQUE,
                created_at INTEGER NOT NULL,
                expires_at INTEGER NOT NULL,
                consumed_at INTEGER,
                consumed_by_agent_name TEXT,
                created_by_user TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_vouchers_hash ON vouchers(secret_hash);
        """;
        cmd.ExecuteNonQuery();

        // V2a: extend the agents table with enrollment lineage. ALTER ADD
        // COLUMN is idempotent-by-existence-check in SQLite via PRAGMA.
        AddColumnIfMissing(c, "agents", "enrolled_at", "INTEGER");
        AddColumnIfMissing(c, "agents", "enrolled_via", "INTEGER");
        AddColumnIfMissing(c, "agents", "revoked_at", "INTEGER");
    }

    /// <summary>
    /// Add a column if it doesn't already exist. SQLite has no
    /// <c>ADD COLUMN IF NOT EXISTS</c>; the workaround is to query
    /// <c>PRAGMA table_info</c> and only run the ALTER when the column
    /// isn't there. Used by V2a schema migration so re-running the hub
    /// against an older hub.db doesn't fail at startup.
    /// </summary>
    private static void AddColumnIfMissing(SqliteConnection c, string table, string column, string sqlType)
    {
        using var probe = c.CreateCommand();
        probe.CommandText = $"PRAGMA table_info({table})";
        using var reader = probe.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        using var alter = c.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {sqlType}";
        alter.ExecuteNonQuery();
    }

    private void Load()
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, url, token, last_seen FROM agents";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(0);
            var url = r.GetString(1);
            var token = r.IsDBNull(2) ? null : r.GetString(2);
            var lastSeen = r.IsDBNull(3) ? (DateTimeOffset?)null
                : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3));
            _agents[name] = new AgentInfo(name, url, false, null, lastSeen);
            if (token is not null) _tokens[name] = token;
        }
        _logger.LogInformation("Loaded {N} agents from {Db}", _agents.Count, _dbPath);
    }

    public IReadOnlyList<AgentInfo> List()
    {
        lock (_lock)
        {
            // Reflect freshness: an agent we haven't heard from in _staleAfter
            // is presumed offline regardless of its last persisted Online flag.
            var now = DateTimeOffset.UtcNow;
            return _agents.Values
                .Select(a => a with { Online = a.Online && a.LastSeen is { } ls && (now - ls) <= _staleAfter })
                .ToList();
        }
    }

    public AgentInfo? Get(string name)
    {
        lock (_lock) return _agents.GetValueOrDefault(name);
    }

    public string? GetToken(string name)
    {
        lock (_lock) return _tokens.GetValueOrDefault(name);
    }

    public void MarkOnline(string name)
    {
        lock (_lock)
        {
            if (_agents.TryGetValue(name, out var a))
                _agents[name] = a with { Online = true, LastSeen = DateTimeOffset.UtcNow };
        }
    }

    public void MarkOffline(string name)
    {
        lock (_lock)
        {
            if (_agents.TryGetValue(name, out var a))
                _agents[name] = a with { Online = false };
        }
    }

    public void Upsert(string name, string url, string? token, bool online, IReadOnlyList<string>? flavors = null)
    {
        // Preserve previously-known flavors if this caller didn't specify any
        // (e.g. a manual /api/agents POST without discovery context).
        var resolvedFlavors = flavors;
        if (resolvedFlavors is null)
        {
            lock (_lock)
            {
                if (_agents.TryGetValue(name, out var existing))
                {
                    resolvedFlavors = existing.Flavors;
                }
            }
        }

        var info = new AgentInfo(name, url, online, null, DateTimeOffset.UtcNow, resolvedFlavors);
        lock (_lock)
        {
            _agents[name] = info;
            if (token is not null) _tokens[name] = token;
        }
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agents (name, url, token, last_seen)
            VALUES ($name, $url, $token, $ts)
            ON CONFLICT(name) DO UPDATE SET url=excluded.url,
              token = COALESCE(excluded.token, agents.token),
              last_seen = excluded.last_seen
        """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$token", (object?)token ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public bool Remove(string name)
    {
        bool removed;
        lock (_lock)
        {
            removed = _agents.Remove(name);
            _tokens.Remove(name);
        }
        if (!removed) return false;
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM agents WHERE name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.ExecuteNonQuery();
        return true;
    }
}
