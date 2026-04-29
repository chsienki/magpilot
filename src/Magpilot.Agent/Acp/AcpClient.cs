using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Magpilot.Agent.Acp;

/// <summary>
/// JSON-RPC 2.0 client for a single <c>copilot --acp</c> child process over stdio.
/// One instance per host; multiplexes any number of ACP sessionIds.
/// </summary>
public sealed class AcpClient : IAsyncDisposable
{
    private readonly ILogger<AcpClient> _logger;
    private readonly string _copilotExe;
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

    public AcpClient(ILogger<AcpClient> logger, string? copilotExe = null)
    {
        _logger = logger;
        _copilotExe = copilotExe ?? (OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_copilotExe, "--acp --allow-all-tools")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start copilot");
        _logger.LogInformation("Started copilot --acp pid={Pid}", _proc.Id);

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
        await Task.CompletedTask;
    }
}
