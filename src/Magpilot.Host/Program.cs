using System.Diagnostics;
using Magpilot.Host;
using Magpilot.Shared.Models;

// magpilot launcher (assembly: magpilot, project: Magpilot.Host) -- thin
// wrapper around `copilot` that coordinates with magpilot-agent so a session
// is driven by exactly one process at a time.
//
// See magpilot-shim project doc in copilot-context for the full design.

WrapperOptions opts;
try { opts = WrapperOptions.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"magpilot: {ex.Message}");
    return 2;
}

if (opts.Help)
{
    Console.WriteLine(WrapperOptions.HelpText);
    return 0;
}

if (opts.Version)
{
    return await VersionPrinter.RunAsync();
}

if (opts.Update)
{
    return await UpdateInstaller.RunAsync();
}

// --magpilot-skip-check wins over everything: degrade to a transparent
// pass-through that just exec's the real copilot.
if (opts.SkipCheck)
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);

// Best-effort: ask the local agent if a newer release is out and surface
// it as a one-line banner. Fast (~500ms cap), silent on every error path,
// and skipped under --magpilot-skip-check above.
await UpdateBanner.MaybePrintAsync();

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
    Console.Error.WriteLine($"magpilot: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"magpilot: agent unreachable ({(agent?.BaseUrl ?? "?")}: {ex.GetType().Name}: {ex.Message}).");
    Console.Error.WriteLine("magpilot: falling through. Use --magpilot-skip-check to silence.");
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
    Console.Error.WriteLine($"magpilot: GET /state failed ({ex.GetType().Name}: {ex.Message}). Falling through.");
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
        Console.Error.WriteLine($"magpilot: {ex.Message}");
        agent.Dispose();
        return 3;
    }

    if (choice == TakeOverPrompt.Choice.No)
    {
        Console.WriteLine("magpilot: not taking over. Exiting.");
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
    Console.WriteLine($"magpilot: {(force ? "force-acquiring" : "waiting for current turn to finish")}...");
    try
    {
        state = await agent.AcquireForHostAsync(sid, hostPid, force);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"magpilot: acquire failed ({ex.GetType().Name}: {ex.Message}).");
        agent.Dispose();
        return 4;
    }
    Console.WriteLine($"magpilot: acquired (owner={state.Owner}). starting copilot...");
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
        Console.Error.WriteLine($"magpilot: {ex.Message}");
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
        Console.Error.WriteLine($"magpilot: {ex.Message}");
        agent.Dispose();
        return 127;
    }

    var hostPid = Environment.ProcessId;

    while (true)
    {
        // Build copilot argv: forward whatever the user passed, ensuring
        // --resume=<sid> is in there exactly once.
        var argv = WithResumeFlag(opts.ForwardArgs, sid);

        // Spawn copilot inside a PTY. PtyHost wires up stdin/stdout
        // pumping and puts our terminal in raw mode so copilot's TUI
        // sees keystrokes verbatim. Disposing the PtyHost restores the
        // terminal mode -- so we always wrap in await using.
        PtyHost copilotHost;
        try
        {
            copilotHost = await PtyHost.SpawnAsync(copilotPath, argv, Environment.CurrentDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: failed to spawn copilot in PTY: {ex.Message}");
            agent.Dispose();
            return 4;
        }

        await using (copilotHost)
        {
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
                    Console.Error.WriteLine($"magpilot: SSE subscribe failed: {ex.Message}");
                }
            });

            var done = await Task.WhenAny(copilotHost.ExitTask, preempted.Task);
            sseCts.Cancel();

            if (done == copilotHost.ExitTask)
            {
                // Child exited on its own. Release ownership and return.
                var exit = await copilotHost.ExitTask;
                try { await agent.ReleaseAsync(sid, hostPid); }
                catch (Exception ex) { Console.Error.WriteLine($"magpilot: release failed: {ex.Message}"); }
                agent.Dispose();
                return exit;
            }

            // SSE arrived first -- web is preempting us.
            var rrEvt = await preempted.Task;
            await copilotHost.ShutdownGracefullyAsync(TimeSpan.FromSeconds(rrEvt.Force ? 1 : 3));
            // Banner uses Carriage Return to start at column 0 in case
            // copilot left the cursor mid-line. Newlines are \r\n because
            // the parent terminal is still in raw mode at this point.
            Console.Out.Write("\r\n─── web took over this session ───\r\n");
            Console.Out.Write($"   requester: {rrEvt.Requester}{(rrEvt.Force ? " (force)" : "")}\r\n");
            Console.Out.Flush();

            try { await agent.ReleaseAsync(sid, hostPid); }
            catch (Exception ex) { Console.Error.WriteLine($"magpilot: release failed: {ex.Message}"); }
        }
        // PtyHost disposed here -- raw mode restored, cooked mode back.

        if (opts.ExitOnHandoff)
        {
            agent.Dispose();
            return 0;
        }

        // Sit on the resume prompt with a 10-min timeout. Console is
        // back in cooked mode so Console.ReadLine works normally.
        Console.WriteLine();
        var timeout = TimeSpan.FromMinutes(10);
        Console.WriteLine($"  Press <enter> to take it back, or wait {(int)timeout.TotalMinutes}:00 to auto-exit");

        var pressTask = Task.Run(() => { try { Console.ReadLine(); } catch { } });
        var winner = await Task.WhenAny(pressTask, Task.Delay(timeout));
        if (winner != pressTask)
        {
            Console.WriteLine("magpilot: timed out. Exiting.");
            agent.Dispose();
            return 0;
        }

        // User pressed enter: re-acquire (polite by default) and loop
        // back to spawn a fresh copilot --resume.
        Console.WriteLine("magpilot: requesting take-back...");
        try
        {
            await agent.AcquireForHostAsync(sid, hostPid, force: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: take-back failed: {ex.Message}");
            agent.Dispose();
            return 5;
        }
        Console.WriteLine("magpilot: reconnected. resuming copilot...");
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
