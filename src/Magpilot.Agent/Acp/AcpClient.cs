using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Magpilot.Agent.Acp;

/// <summary>
/// JSON-RPC 2.0 client for a single <c>copilot --acp</c> child process over stdio.
/// Each instance owns one process. The session manager runs multiple instances
/// in parallel (one per <see cref="AcpFlavor"/>), routing each session to its
/// owning client by sessionId.
/// </summary>
public sealed class AcpClient : IAsyncDisposable
{
    private readonly ILogger<AcpClient> _logger;
    private readonly string _exe;
    private readonly string _args;
    private Process? _proc;
    private int _nextId;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly object _pendingLock = new();
    private readonly Channel<JsonObject> _outgoing = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    public event Action<string, JsonNode?>? OnSessionUpdate;
    public event Func<string, JsonNode?, Task<JsonNode>>? OnRequest;

    public AcpClient(ILogger<AcpClient> logger, string? exe = null, string? args = null)
    {
        _logger = logger;
        _exe = exe ?? (OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
        _args = args ?? "--acp --allow-all-tools";
    }

    /// <summary>
    /// True while the underlying <c>copilot --acp</c> subprocess is alive.
    /// Returns false before <see cref="StartAsync"/> succeeds and after the
    /// child exits (whether crashed, killed, or disposed). Used by
    /// <see cref="AcpFlavorPool"/> to skip-and-respawn cached-but-dead
    /// multiplex clients instead of writing into a broken pipe and
    /// timing out 120s later.
    /// </summary>
    public bool IsAlive => _proc is { HasExited: false };

    public async Task StartAsync(CancellationToken ct)
    {
        // Resolve the executable to a full path before spawning. .NET's
        // Process.Start uses the OS's CreateProcess for unqualified names,
        // which on Windows can resolve to a launcher shim that immediately
        // re-execs and exits, leaving us with broken pipes. Resolving via
        // PATH ourselves and passing a full path means CreateProcess just
        // launches that exact binary -- the same one the user gets at the
        // command line.
        var resolvedExe = ResolveOnPath(_exe) ?? _exe;

        // The Copilot CLI's --acp mode does NOT auto-load enabled plugins
        // from settings.json the way the interactive CLI does. Discover the
        // user's enabled plugins ourselves and append --plugin-dir for each
        // so skills like task-context, revue etc are available inside ACP
        // sessions just as they are at the interactive prompt.
        var pluginArgs = BuildPluginDirArgs();
        var fullArgs = string.IsNullOrEmpty(pluginArgs) ? _args : $"{_args} {pluginArgs}";

        var psi = new ProcessStartInfo(resolvedExe, fullArgs)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // The agent is now a Windows-subsystem app (no console of its
            // own). Without CreateNoWindow, Windows would allocate a new
            // console for this console-mode copilot child and flash it on
            // screen each time we spawn one. With stdio fully redirected
            // it serves no purpose anyway.
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {resolvedExe}");
        _logger.LogInformation("Started {Exe} {Args} pid={Pid}", resolvedExe, fullArgs, _proc.Id);

        // On Windows, immediately enroll the child in our process-wide
        // kill-on-close Job Object so it (and any grandchildren it
        // spawns) die with the agent. Linux gets the same behaviour
        // automatically through process-group inheritance. See
        // Win32JobObject for full design rationale.
        if (OperatingSystem.IsWindows())
        {
            Win32JobObject.Attach(_proc, _logger);
        }

        _ = Task.Run(() => DrainStderrAsync(ct));
        _ = Task.Run(() => ReadLoopAsync(ct));
        _ = Task.Run(() => WriteLoopAsync(ct));

        var init = await CallAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
        }, ct);
        _logger.LogInformation("ACP initialized: {Result}", init?["agentInfo"]?.ToJsonString());
    }

    public Task<JsonNode?> CallAsync(string method, JsonObject? @params, CancellationToken ct, int timeoutSec = 120)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[id] = tcs;
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null) req["params"] = @params;
        if (!_outgoing.Writer.TryWrite(req))
            throw new InvalidOperationException("Outgoing channel closed");

        return WaitWithTimeoutAsync(id, tcs.Task, ct, timeoutSec);
    }

    public Task NotifyAsync(string method, JsonObject @params)
    {
        var note = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        };
        _outgoing.Writer.TryWrite(note);
        return Task.CompletedTask;
    }

    private async Task<JsonNode?> WaitWithTimeoutAsync(int id, Task<JsonNode?> task, CancellationToken ct, int timeoutSec)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var done = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, linked.Token));
            if (done != task)
            {
                lock (_pendingLock) _pending.Remove(id);
                throw new TimeoutException($"ACP call id={id} timed out");
            }
            return await task;
        }
        catch
        {
            lock (_pendingLock) _pending.Remove(id);
            throw;
        }
    }

    private async Task DrainStderrAsync(CancellationToken ct)
    {
        if (_proc is null) return;
        string? line;
        while ((line = await _proc.StandardError.ReadLineAsync(ct)) != null)
            _logger.LogDebug("[copilot stderr] {Line}", line);
    }

    /// <summary>
    /// Resolve an unqualified executable name to a full path by walking the
    /// PATH environment variable. Returns null if no match found.
    /// On Windows, also tries the .exe extension if the input lacks one.
    /// </summary>
    private static string? ResolveOnPath(string exe)
    {
        if (Path.IsPathFullyQualified(exe) && File.Exists(exe)) return exe;
        var pathSep = OperatingSystem.IsWindows() ? ';' : ':';
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(pathSep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = OperatingSystem.IsWindows() && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { exe, exe + ".exe" }
            : new[] { exe };
        foreach (var dir in dirs)
        {
            foreach (var cand in candidates)
            {
                var full = Path.Combine(dir, cand);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    /// <summary>
    /// Read ~/.copilot/settings.json and emit a <c>--plugin-dir &lt;path&gt;</c>
    /// flag for each enabled plugin's cache directory. The Copilot CLI's
    /// <c>--acp</c> mode does not auto-load plugins from settings the way
    /// the interactive CLI does, so we forward them explicitly.
    /// </summary>
    private string BuildPluginDirArgs()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetEnvironmentVariable("USERPROFILE")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return "";

            var settingsPath = Path.Combine(home, ".copilot", "settings.json");
            if (!File.Exists(settingsPath)) return "";

            using var fs = File.OpenRead(settingsPath);
            var root = JsonNode.Parse(fs);
            if (root is null) return "";

            // settings.json schemas we have to handle:
            //
            //   OLD (pre-2026-05): installedPlugins is an array; each
            //   entry has { owner, name, enabled, cache_path }.
            //
            //   NEW (2026-05+):    enabledPlugins is a string-bool map
            //   keyed by "<name>@<owner>" -> true. Cache paths are NOT
            //   in settings.json anymore; the convention is
            //   <home>/.copilot/installed-plugins/<owner>/<name>/.
            //
            // Try the new schema first (current default), fall back to
            // the old one so any user who hasn't been migrated still
            // gets skills loaded.
            var args = new StringBuilder();
            int loaded = 0;
            var pluginsRoot = Path.Combine(home, ".copilot", "installed-plugins");

            if (root["enabledPlugins"] is JsonObject enabled)
            {
                foreach (var kvp in enabled)
                {
                    if (kvp.Value?.GetValue<bool>() != true) continue;
                    var key = kvp.Key;
                    // Key shape: "<name>@<owner>". Split on the last '@'
                    // in case the name itself contains one (defensive).
                    var at = key.LastIndexOf('@');
                    if (at <= 0 || at >= key.Length - 1) continue;
                    var name  = key[..at];
                    var owner = key[(at + 1)..];
                    var cache = Path.Combine(pluginsRoot, owner, name);
                    if (!Directory.Exists(cache)) continue;
                    if (args.Length > 0) args.Append(' ');
                    args.Append("--plugin-dir ");
                    args.Append(QuoteIfNeeded(cache));
                    loaded++;
                }
            }
            else if (root["installedPlugins"] is JsonArray plugins)
            {
                foreach (var p in plugins)
                {
                    if (p is null) continue;
                    if (p["enabled"]?.GetValue<bool>() != true) continue;
                    var cache = p["cache_path"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(cache) || !Directory.Exists(cache)) continue;
                    if (args.Length > 0) args.Append(' ');
                    args.Append("--plugin-dir ");
                    args.Append(QuoteIfNeeded(cache));
                    loaded++;
                }
            }
            else
            {
                _logger.LogWarning(
                    "settings.json has neither enabledPlugins nor installedPlugins; ACP child will run without skills");
                return "";
            }

            if (loaded > 0)
                _logger.LogInformation("Forwarding {Count} enabled plugin(s) to ACP child via --plugin-dir", loaded);
            else
                _logger.LogWarning(
                    "settings.json had enabledPlugins/installedPlugins but 0 resolved to existing plugin-dirs; " +
                    "expected layout is {Root}/<owner>/<name>/", pluginsRoot);
            return args.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover enabled plugins from settings.json; ACP child will run without them");
            return "";
        }
    }

    private static string QuoteIfNeeded(string s) =>
        s.Contains(' ') ? $"\"{s}\"" : s;

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        if (_proc is null) return;
        await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct))
        {
            var s = msg.ToJsonString();
            await _proc.StandardInput.WriteLineAsync(s.AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_proc is null) return;
        try
        {
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch (Exception ex) { _logger.LogWarning(ex, "ACP parse failure: {Line}", line); continue; }
                if (msg is null) continue;
                await DispatchAsync(msg, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ACP read loop crashed");
        }
        finally
        {
            // ReadLineAsync returned null (stdout closed -> child exited) or
            // the loop crashed. Either way the subprocess is gone. Surface
            // that to every in-flight request immediately instead of
            // letting them sit on the 120s WaitWithTimeoutAsync clock.
            FailAllPending(new IOException(
                $"ACP child process exited (pid={_proc?.Id}, exitCode={SafeExitCode()})"));
        }
    }

    private int? SafeExitCode()
    {
        try { return _proc?.HasExited == true ? _proc.ExitCode : (int?)null; }
        catch { return null; }
    }

    private void FailAllPending(Exception ex)
    {
        List<TaskCompletionSource<JsonNode?>> snapshot;
        lock (_pendingLock)
        {
            snapshot = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var tcs in snapshot) tcs.TrySetException(ex);
    }

    private async Task DispatchAsync(JsonNode msg, CancellationToken ct)
    {
        var idNode = msg["id"];
        var methodNode = msg["method"];

        if (idNode is not null && methodNode is null)
        {
            // Response
            var id = idNode.GetValue<int>();
            TaskCompletionSource<JsonNode?>? tcs;
            lock (_pendingLock) _pending.Remove(id, out tcs);
            if (tcs is null) return;
            if (msg["error"] is JsonNode err)
                tcs.TrySetException(new Exception($"ACP error: {err.ToJsonString()}"));
            else
                tcs.TrySetResult(msg["result"]);
            return;
        }

        if (methodNode is null) return;
        var method = methodNode.GetValue<string>();
        var @params = msg["params"];

        if (idNode is null)
        {
            // Notification (e.g. session/update)
            if (method == "session/update")
            {
                var sid = @params?["sessionId"]?.GetValue<string>();
                if (sid is not null)
                {
                    try { OnSessionUpdate?.Invoke(sid, @params?["update"]); }
                    catch (Exception ex) { _logger.LogError(ex, "OnSessionUpdate handler threw"); }
                }
            }
            return;
        }

        // Server-to-client request (e.g. session/request_permission)
        var reqId = idNode.GetValue<int>();
        JsonNode result;
        try
        {
            var handler = OnRequest;
            if (handler is null)
            {
                result = new JsonObject();
            }
            else
            {
                result = await handler(method, @params);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnRequest handler threw for {Method}", method);
            var err = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = reqId,
                ["error"] = new JsonObject { ["code"] = -32000, ["message"] = ex.Message },
            };
            _outgoing.Writer.TryWrite(err);
            return;
        }
        var ok = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = reqId,
            ["result"] = result,
        };
        _outgoing.Writer.TryWrite(ok);
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        if (_proc is not null)
        {
            try { _proc.StandardInput.Close(); } catch { }
            try { if (!_proc.WaitForExit(2000)) _proc.Kill(); } catch { }
            _proc.Dispose();
        }
        FailAllPending(new ObjectDisposedException(nameof(AcpClient)));
        await Task.CompletedTask;
    }
}
