using System.Diagnostics;
using Magpilot.Host;
using Magpilot.Shared.Models;

// magpilot-host -- thin wrapper around `copilot` that coordinates with
// magpilot-agent so a session is driven by exactly one process at a time.
//
// See magpilot-shim project doc in copilot-context for the full design.

WrapperOptions opts;
try { opts = WrapperOptions.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"magpilot-host: {ex.Message}");
    return 2;
}

if (opts.Help)
{
    Console.WriteLine(WrapperOptions.HelpText);
    return 0;
}

// --magpilot-skip-check wins over everything: degrade to a transparent
// pass-through that just exec's the real copilot.
if (opts.SkipCheck)
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);

// Try to talk to the agent. If unreachable, fall through to skip-check
// behavior (with a warning) so an agent outage never blocks the user.
AgentClient? agent = null;
try
{
    agent = new AgentClient();
    using var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await agent.PingAsync(pingCts.Token);
}
catch (InvalidOperationException ex)
{
    // Missing token etc. -- this is user error, not agent down.
    Console.Error.WriteLine($"magpilot-host: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"magpilot-host: agent unreachable ({(agent?.BaseUrl ?? "?")}: {ex.GetType().Name}: {ex.Message}).");
    Console.Error.WriteLine("magpilot-host: falling through. Use --magpilot-skip-check to silence.");
    agent?.Dispose();
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);
}

if (opts.Status)
{
    Console.WriteLine($"agent reachable: {agent.BaseUrl}");
    Console.WriteLine("(--magpilot-status full session listing not implemented yet)");
    agent.Dispose();
    return 0;
}

// Resolve the session id from forward args. Phase-2 v1 only handles the
// explicit --resume=<sid> case; --continue and the no-args picker are
// follow-ups (we just spawn fresh copilot for those).
var sid = opts.ExtractResumeSessionId();
if (string.IsNullOrEmpty(sid))
{
    // No specific session to coordinate -- just exec real copilot.
    // Fresh sessions don't need the take-over prompt.
    agent.Dispose();
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);
}

// We have a target session. Look it up.
SessionStateInfo? state;
try { state = await agent.GetStateAsync(sid); }
catch (Exception ex)
{
    Console.Error.WriteLine($"magpilot-host: GET /state failed ({ex.GetType().Name}: {ex.Message}). Falling through.");
    agent.Dispose();
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);
}

if (state is null)
{
    // Session unknown to the agent (not on disk). Let copilot deal with
    // it -- it'll either find it locally or error helpfully.
    agent.Dispose();
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);
}

// If owned by the agent or another host, prompt to take over.
if (state.Owner == SessionOwner.Agent || state.Owner == SessionOwner.Host || state.Owner == SessionOwner.External)
{
    TakeOverPrompt.Choice choice;
    try { choice = TakeOverPrompt.Ask(state, opts); }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"magpilot-host: {ex.Message}");
        agent.Dispose();
        return 3;
    }

    if (choice == TakeOverPrompt.Choice.No)
    {
        Console.WriteLine("magpilot-host: not taking over. Exiting.");
        agent.Dispose();
        return 0;
    }

    if (choice == TakeOverPrompt.Choice.Details)
    {
        // TODO: dump last 5-10 events from the SSE; for now just re-render.
        TakeOverPrompt.Render(state);
        choice = TakeOverPrompt.Ask(state, opts with { });
        if (choice == TakeOverPrompt.Choice.No) { agent.Dispose(); return 0; }
    }

    var force = choice == TakeOverPrompt.Choice.Force;
    var hostPid = Environment.ProcessId;
    Console.WriteLine($"magpilot-host: {(force ? "force-acquiring" : "waiting for current turn to finish")}...");
    try
    {
        state = await agent.AcquireForHostAsync(sid, hostPid, force);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"magpilot-host: acquire failed ({ex.GetType().Name}: {ex.Message}).");
        agent.Dispose();
        return 4;
    }
    Console.WriteLine($"magpilot-host: acquired (owner={state.Owner}). starting copilot...");
}

// Spawn copilot --resume=<sid> with the user's terminal. Then watch for
// either copilot exiting on its own OR the agent firing release_requested.
return await RunSessionLoopAsync(agent, sid, opts);


// ----------------------------------------------------------------------
// Helpers
// ----------------------------------------------------------------------

static async Task<int> ExecRealCopilotAsync(IReadOnlyList<string> forwardArgs, AgentClient? agentClient)
{
    string copilotPath;
    try { copilotPath = CopilotLocator.Find(); }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"magpilot-host: {ex.Message}");
        return 127;
    }

    var psi = new ProcessStartInfo
    {
        FileName = copilotPath,
        UseShellExecute = false,
        // Inherit our stdin/stdout/stderr so copilot owns the TTY directly.
        RedirectStandardInput  = false,
        RedirectStandardOutput = false,
        RedirectStandardError  = false,
    };
    foreach (var a in forwardArgs) psi.ArgumentList.Add(a);

    using var p = Process.Start(psi)
        ?? throw new InvalidOperationException($"Failed to start {copilotPath}");
    await p.WaitForExitAsync();
    agentClient?.Dispose();
    return p.ExitCode;
}

static async Task<int> RunSessionLoopAsync(AgentClient agent, string sid, WrapperOptions opts)
{
    string copilotPath;
    try { copilotPath = CopilotLocator.Find(); }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"magpilot-host: {ex.Message}");
        agent.Dispose();
        return 127;
    }

    var hostPid = Environment.ProcessId;

    while (true)
    {
        // Build copilot argv: forward whatever the user passed, ensuring
        // --resume=<sid> is in there exactly once.
        var argv = WithResumeFlag(opts.ForwardArgs, sid);

        var psi = new ProcessStartInfo
        {
            FileName = copilotPath,
            UseShellExecute = false,
            RedirectStandardInput  = false,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var copilot = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {copilotPath}");

        // Listen for release-requested in the background; signal via cts.
        using var sseCts = new CancellationTokenSource();
        var preempted = new TaskCompletionSource<ReleaseRequested>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in agent.SubscribeAsync(sid, sseCts.Token))
                {
                    if (evt is ReleaseRequested rr)
                    {
                        preempted.TrySetResult(rr);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Don't crash -- if the SSE drops we just lose preemption
                // for this session, which is degraded but not fatal.
                Console.Error.WriteLine($"magpilot-host: SSE subscribe failed: {ex.Message}");
            }
        });

        var copilotExit = copilot.WaitForExitAsync();
        var done = await Task.WhenAny(copilotExit, preempted.Task);
        sseCts.Cancel();

        if (done == copilotExit)
        {
            // Child exited on its own. Release ownership and return.
            try { await agent.ReleaseAsync(sid, hostPid); }
            catch (Exception ex) { Console.Error.WriteLine($"magpilot-host: release failed: {ex.Message}"); }
            agent.Dispose();
            return copilot.ExitCode;
        }

        // SSE arrived first -- web is preempting us.
        var rrEvt = await preempted.Task;
        Console.WriteLine();
        Console.WriteLine("─── web took over this session ───");
        Console.WriteLine($"   requester: {rrEvt.Requester}{(rrEvt.Force ? " (force)" : "")}");
        // Send Ctrl+Break / SIGTERM to copilot (best v1 effort -- we
        // don't have a PTY to inject /exit into stdin).
        try
        {
            // Process.Kill(true) on .NET 9 uses TerminateProcess on Windows,
            // SIGKILL on Unix. We want graceful first; try SIGTERM (Unix)
            // or Ctrl+C event (Windows) before that.
            if (OperatingSystem.IsWindows())
            {
                // Best we can do without PTY: kill the process tree.
                copilot.Kill(entireProcessTree: true);
            }
            else
            {
                // SIGTERM by .NET API name on Unix: send via Process.Kill(false)
                // is actually SIGKILL on Linux too. We have to P/Invoke kill(2)
                // ourselves to send SIGTERM, but for v1 just use Kill().
                copilot.Kill(entireProcessTree: true);
            }
        }
        catch { /* already dead */ }
        try { await copilot.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        try { await agent.ReleaseAsync(sid, hostPid); }
        catch (Exception ex) { Console.Error.WriteLine($"magpilot-host: release failed: {ex.Message}"); }

        if (opts.ExitOnHandoff)
        {
            agent.Dispose();
            return 0;
        }

        // Sit on the resume prompt with a 10-min timeout.
        Console.WriteLine();
        var timeout = TimeSpan.FromMinutes(10);
        Console.WriteLine($"  Press <enter> to take it back, or wait {(int)timeout.TotalMinutes}:00 to auto-exit");

        var pressTask = Task.Run(() => { try { Console.ReadLine(); } catch { } });
        var winner = await Task.WhenAny(pressTask, Task.Delay(timeout));
        if (winner != pressTask)
        {
            Console.WriteLine("magpilot-host: timed out. Exiting.");
            agent.Dispose();
            return 0;
        }

        // User pressed enter: re-acquire (polite by default) and loop
        // back to spawn a fresh copilot --resume.
        Console.WriteLine("magpilot-host: requesting take-back...");
        try
        {
            await agent.AcquireForHostAsync(sid, hostPid, force: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot-host: take-back failed: {ex.Message}");
            agent.Dispose();
            return 5;
        }
        Console.WriteLine("magpilot-host: reconnected. resuming copilot...");
        // Loop -> spawn copilot again
    }
}

static IReadOnlyList<string> WithResumeFlag(IReadOnlyList<string> forwardArgs, string sid)
{
    // If forwardArgs already includes --resume=<sid> or --resume <sid>,
    // pass through as-is. Otherwise prepend --resume=<sid>.
    foreach (var a in forwardArgs)
    {
        if (a.StartsWith("--resume", StringComparison.Ordinal)) return forwardArgs;
    }
    var copy = new List<string>(forwardArgs.Count + 1) { $"--resume={sid}" };
    copy.AddRange(forwardArgs);
    return copy;
}
