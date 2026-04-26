using Microsoft.Data.Sqlite;
using Clawpilot.Shared.Models;

namespace Clawpilot.Hub.Agents;

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

    public AgentRegistry(IConfiguration config, ILogger<AgentRegistry> logger)
    {
        _logger = logger;
        var dataDir = config["Hub:DataDir"]
            ?? Environment.GetEnvironmentVariable("CLAWPILOT_HUB_DATA")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "hub.db");
        InitDb();
        Load();
    }

    private string ConnString => $"Data Source={_dbPath}";

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
        """;
        cmd.ExecuteNonQuery();
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
        lock (_lock) return _agents.Values.ToList();
    }

    public AgentInfo? Get(string name)
    {
        lock (_lock) return _agents.GetValueOrDefault(name);
    }

    public string? GetToken(string name)
    {
        lock (_lock) return _tokens.GetValueOrDefault(name);
    }

    public void Upsert(string name, string url, string? token, bool online)
    {
        var info = new AgentInfo(name, url, online, null, DateTimeOffset.UtcNow);
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
