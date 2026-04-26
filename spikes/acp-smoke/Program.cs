// Spike A: ACP smoke test over stdio.
// Verifies: ACP sessionId == on-disk ~/.copilot/session-state/<id>/ UUID.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

var copilotExe = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";

Console.WriteLine($"[spike] launching: {copilotExe} --acp --allow-all-tools");
var psi = new ProcessStartInfo(copilotExe, "--acp --allow-all-tools")
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    StandardInputEncoding = Encoding.UTF8,
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding = Encoding.UTF8,
};
var proc = Process.Start(psi)!;

_ = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardError.ReadLineAsync()) != null)
        Console.Error.WriteLine($"[stderr] {Truncate(line, 300)}");
});

var nextId = 1;
var pending = new Dictionary<int, TaskCompletionSource<JsonNode?>>();

_ = Task.Run(async () =>
{
    string? line;
    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        Console.WriteLine($"[<-] {Truncate(line, 400)}");
        JsonNode? msg;
        try { msg = JsonNode.Parse(line); } catch { continue; }
        if (msg is null) continue;

        var idNode = msg["id"];
        var methodNode = msg["method"];

        if (idNode is not null && methodNode is null)
        {
            // response to one of our calls
            if (pending.Remove(idNode.GetValue<int>(), out var tcs))
            {
                if (msg["error"] is JsonNode err)
                    tcs.SetException(new Exception($"RPC error: {err.ToJsonString()}"));
                else
                    tcs.SetResult(msg["result"]);
            }
        }
        else if (idNode is not null && methodNode is not null)
        {
            // server -> client request: auto-allow once for permission requests
            var serverReqId = idNode.GetValue<int>();
            var method = methodNode.GetValue<string>();
            Console.WriteLine($"[server-req {method}] auto-allowing");
            JsonObject result;
            if (method == "session/request_permission")
            {
                // Pick the first option labelled "allow*" if present, else first option
                var opts = msg["params"]?["options"] as JsonArray;
                string optionId = "allow_once";
                if (opts is not null)
                {
                    foreach (var o in opts)
                    {
                        var oid = o?["optionId"]?.GetValue<string>();
                        if (oid is not null && oid.StartsWith("allow")) { optionId = oid; break; }
                    }
                    if (opts.Count > 0)
                        optionId = opts.FirstOrDefault(o => (o?["optionId"]?.GetValue<string>() ?? "").StartsWith("allow"))
                            ?["optionId"]?.GetValue<string>() ?? opts[0]!["optionId"]!.GetValue<string>();
                }
                result = new JsonObject
                {
                    ["outcome"] = new JsonObject
                    {
                        ["outcome"] = "selected",
                        ["optionId"] = optionId,
                    }
                };
            }
            else
            {
                result = new JsonObject();
            }
            await SendRawAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = serverReqId,
                ["result"] = result,
            });
        }
        // pure notifications: nothing to do
    }
});

async Task<JsonNode?> CallAsync(string method, JsonObject? @params = null, int timeoutSec = 90)
{
    var id = nextId++;
    var tcs = new TaskCompletionSource<JsonNode?>();
    pending[id] = tcs;
    var req = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
    };
    if (@params is not null) req["params"] = @params;
    await SendRawAsync(req);
    var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
    if (done != tcs.Task) throw new TimeoutException($"timeout on {method}");
    return await tcs.Task;
}

async Task SendRawAsync(JsonNode node)
{
    var s = node.ToJsonString();
    Console.WriteLine($"[->] {Truncate(s, 400)}");
    await proc.StandardInput.WriteLineAsync(s);
    await proc.StandardInput.FlushAsync();
}

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

string sessionStateRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".copilot", "session-state");

try
{
    Console.WriteLine("\n=== 1. initialize ===");
    var init = await CallAsync("initialize", new JsonObject
    {
        ["protocolVersion"] = 1,
        ["clientCapabilities"] = new JsonObject
        {
            ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
            ["terminal"] = false,
        }
    });
    Console.WriteLine($"init result: {init?.ToJsonString()}");

    Console.WriteLine("\n=== 2. session/new ===");
    var beforeDirs = Directory.Exists(sessionStateRoot)
        ? new HashSet<string>(Directory.GetDirectories(sessionStateRoot).Select(Path.GetFileName)!)
        : new HashSet<string>();

    var sess = await CallAsync("session/new", new JsonObject
    {
        ["cwd"] = Directory.GetCurrentDirectory(),
        ["mcpServers"] = new JsonArray()
    });
    var sessionId = sess?["sessionId"]?.GetValue<string>();
    Console.WriteLine($"sessionId = {sessionId}");

    var stateDir = Path.Combine(sessionStateRoot, sessionId ?? "");
    Console.WriteLine($"state dir for sessionId exists: {Directory.Exists(stateDir)}");

    if (!Directory.Exists(stateDir))
    {
        var afterDirs = Directory.GetDirectories(sessionStateRoot).Select(Path.GetFileName).ToHashSet();
        afterDirs.ExceptWith(beforeDirs!);
        Console.WriteLine($"NEW dirs created since session/new: {string.Join(", ", afterDirs)}");
    }

    Console.WriteLine("\n=== 3. session/prompt ===");
    var resp = await CallAsync("session/prompt", new JsonObject
    {
        ["sessionId"] = sessionId,
        ["prompt"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = "Reply with exactly the word PONG and nothing else." }
        }
    }, timeoutSec: 120);
    Console.WriteLine($"prompt result: {Truncate(resp?.ToJsonString() ?? "(null)", 600)}");

    Console.WriteLine("\n=== 4. final state-dir snapshot ===");
    if (Directory.Exists(stateDir))
    {
        foreach (var f in Directory.EnumerateFileSystemEntries(stateDir))
            Console.WriteLine($"  {Path.GetFileName(f)}");
    }
    else
    {
        Console.WriteLine("(state dir still missing for declared sessionId)");
        var ssRoot = new DirectoryInfo(sessionStateRoot);
        foreach (var d in ssRoot.EnumerateDirectories().OrderByDescending(d => d.LastWriteTime).Take(5))
            Console.WriteLine($"  recent: {d.Name}  {d.LastWriteTime:s}");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SPIKE FAILED: {ex}");
}
finally
{
    try { proc.StandardInput.Close(); } catch { }
    try { if (!proc.WaitForExit(5000)) proc.Kill(); } catch { }
}
