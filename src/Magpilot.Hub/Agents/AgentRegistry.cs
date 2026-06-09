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
        cmd.CommandText = "SELECT name, url, token, last_seen, enrolled_at, revoked_at FROM agents";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(0);
            var url = r.GetString(1);
            var token = r.IsDBNull(2) ? null : r.GetString(2);
            var lastSeen = r.IsDBNull(3) ? (DateTimeOffset?)null
                : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3));
            var enrolledAt = r.IsDBNull(4) ? (DateTimeOffset?)null
                : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4));
            var revokedAt = r.IsDBNull(5) ? (DateTimeOffset?)null
                : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5));
            _agents[name] = new AgentInfo(name, url, false, null, lastSeen, null, enrolledAt, revokedAt);
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
            // A revoked agent is never considered Online.
            var now = DateTimeOffset.UtcNow;
            return _agents.Values
                .Select(a => a with
                {
                    Online = a.RevokedAt is null
                        && a.Online
                        && a.LastSeen is { } ls
                        && (now - ls) <= _staleAfter
                })
                .ToList();
        }
    }

    public AgentInfo? Get(string name)
    {
        lock (_lock) return _agents.GetValueOrDefault(name);
    }

    /// <summary>
    /// Per-agent bearer token for the hub's outbound calls. Returns
    /// null when the agent is unknown, has no token (discovered but
    /// not yet enrolled), OR has been revoked. The Proxy wrapper in
    /// <c>HubEndpoints</c> consumes null to short-circuit with a
    /// useful error instead of letting the call hit the wire with
    /// no auth.
    /// </summary>
    public string? GetToken(string name)
    {
        lock (_lock)
        {
            if (_agents.TryGetValue(name, out var info) && info.RevokedAt is not null)
                return null;
            return _tokens.GetValueOrDefault(name);
        }
    }

    /// <summary>Returns true if the agent exists and has been revoked.</summary>
    public bool IsRevoked(string name)
    {
        lock (_lock) return _agents.TryGetValue(name, out var a) && a.RevokedAt is not null;
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
        // Preserve previously-known fields the caller didn't supply.
        // Discovery probes don't know about flavors / enrollment
        // lineage / revocation state -- only the original enrollment
        // does -- so they'd otherwise clobber those on every sweep.
        IReadOnlyList<string>? resolvedFlavors = flavors;
        DateTimeOffset? resolvedEnrolledAt = null;
        DateTimeOffset? resolvedRevokedAt = null;
        lock (_lock)
        {
            if (_agents.TryGetValue(name, out var existing))
            {
                resolvedFlavors ??= existing.Flavors;
                resolvedEnrolledAt = existing.EnrolledAt;
                resolvedRevokedAt = existing.RevokedAt;
            }
        }

        var info = new AgentInfo(name, url, online, null, DateTimeOffset.UtcNow, resolvedFlavors, resolvedEnrolledAt, resolvedRevokedAt);
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

    /// <summary>
    /// Mark the agent as revoked. The in-memory token is cleared so
    /// <see cref="GetToken"/> returns null immediately (no race window
    /// where the next outbound call would still go through). The DB
    /// row is updated atomically; re-pairing via voucher redeem
    /// upserts the row with <c>revoked_at = NULL</c> so revocation is
    /// fully reversible from the user's perspective: revoke + run
    /// <c>magpilot --magpilot-pair=&lt;fresh-voucher&gt;</c>.
    /// </summary>
    public bool Revoke(string name)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!_agents.TryGetValue(name, out var existing))
                return false;
            _agents[name] = existing with { RevokedAt = now, Online = false };
            _tokens.Remove(name);
        }
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE agents SET revoked_at = $now, token = NULL WHERE name = $n
        """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$n", name);
        var changed = cmd.ExecuteNonQuery();
        _logger.LogWarning("Agent {Name} revoked", name);
        return changed > 0;
    }

    /// <summary>
    /// Refresh the in-memory <see cref="AgentInfo"/> for an agent
    /// whose row was updated outside <see cref="Upsert"/> (currently:
    /// the EnrollmentService's redeem flow writes the new
    /// <c>token</c> / <c>enrolled_at</c> / <c>enrolled_via</c> /
    /// <c>revoked_at = NULL</c> directly in its transaction). Reloads
    /// from disk so the next call to <see cref="List"/> /
    /// <see cref="Get"/> reflects the update.
    /// </summary>
    public void Reload(string name)
    {
        using var c = new SqliteConnection(ConnString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT url, token, last_seen, enrolled_at, revoked_at
            FROM agents WHERE name = $n
        """;
        cmd.Parameters.AddWithValue("$n", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return;
        var url = r.GetString(0);
        var token = r.IsDBNull(1) ? null : r.GetString(1);
        var lastSeen = r.IsDBNull(2) ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2));
        var enrolledAt = r.IsDBNull(3) ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3));
        var revokedAt = r.IsDBNull(4) ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4));
        lock (_lock)
        {
            var prevFlavors = _agents.TryGetValue(name, out var prev) ? prev.Flavors : null;
            _agents[name] = new AgentInfo(name, url, false, null, lastSeen, prevFlavors, enrolledAt, revokedAt);
            if (token is not null) _tokens[name] = token;
            else _tokens.Remove(name);
        }
    }
}
