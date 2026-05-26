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

if (opts.Claim is not null)
{
    return await MagpilotClaim.RunAsync(opts.Claim);
}

// --magpilot-skip-check wins over everything: degrade to a transparent
// pass-through that just exec's the real copilot.
if (opts.SkipCheck)
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);

// Best-effort: ask the local agent if a newer release is out and surface
// it as a one-line banner. Fast (~500ms cap), silent on every error path,
// and skipped under --magpilot-skip-check above.
await UpdateBanner.MaybePrintAsync();

// Try to talk to the agent. If unreachable, attempt to start the
// installed MagpilotAgent scheduled task (or fall back to direct exec)
// so users don't have to remember to `Start-ScheduledTask` manually.
// If it still can't be reached, fall through to skip-check behavior
// so an agent outage never blocks the user.
AgentClient? agent = null;
try
{
    agent = new AgentClient();
}
catch (InvalidOperationException ex)
{
    // Missing token etc. -- this is user error, not agent down.
    Console.Error.WriteLine($"magpilot: {ex.Message}");
    return 2;
}

try
{
    using var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    await agent.PingAsync(pingCts.Token);
}
catch (Exception firstPingEx)
{
    var started = await AgentLauncher.EnsureRunningAsync();
    if (!started)
    {
        Console.Error.WriteLine($"magpilot: agent unreachable ({agent.BaseUrl}: {firstPingEx.GetType().Name}: {firstPingEx.Message}).");
        Console.Error.WriteLine("magpilot: falling through. Use --magpilot-skip-check to silence.");
        agent.Dispose();
        return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);
    }

    // Agent came up; verify the bearer still works (rare second failure).
    try
    {
        using var pingCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await agent.PingAsync(pingCts.Token);
    }
    catch (Exception secondPingEx)
    {
        Console.Error.WriteLine($"magpilot: agent answered /healthz but auth ping failed: {secondPingEx.Message}");
        agent.Dispose();
        return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);
    }
}

if (opts.Status)
{
    Console.WriteLine($"agent reachable: {agent.BaseUrl}");
    Console.WriteLine("(--magpilot-status full session listing not implemented yet)");
    agent.Dispose();
    return 0;
}

// Resolve the session id from forward args. We only know it up front
// for explicit --resume=<UUID> -- everything else (--resume="some name",
// --continue, no args + interactive picker, new session) needs the
// post-spawn detection path so we can still register HostOwnership with
// the agent and the SPA shows it as Host-owned instead of Locked.
var sid = opts.ExtractResumeSessionId();

// PTY+detection only works when we own the terminal. Redirected stdio
// (e.g. `echo /help | magpilot`) skips it and falls back to the
// transparent passthrough -- the launcher can't see the bytes anyway.
var canPty = !Console.IsInputRedirected && !Console.IsOutputRedirected;

if (string.IsNullOrEmpty(sid))
{
    if (!canPty)
    {
        // Non-interactive: just exec copilot. No coordination, but the
        // user didn't ask for it.
        agent.Dispose();
        return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null);
    }
    // No specific session known up front. Spawn copilot in a PTY and
    // post-spawn-detect whichever session it ends up holding (fresh,
    // picker-selected, --continue, etc.). Detection times out gracefully
    // if copilot exits before taking a lock (e.g. `magpilot --version`).
    return await RunSessionLoopWithDetectionAsync(agent, opts);
}

// We have a target session id. Look it up.
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
    // Session unknown to the agent (not on disk yet). Either the user
    // passed a name/--continue that copilot will resolve internally, or
    // the session genuinely doesn't exist. Spawn in PTY and post-spawn-
    // detect the resulting session so we can still register HostOwnership
    // and participate in cooperative handoff.
    return await RunSessionLoopWithDetectionAsync(agent, opts);
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

/// <summary>
/// Spawn copilot WITHOUT knowing the session id up front (user passed
/// --resume=&lt;name&gt;, --continue, no flag at all, etc.). Post-spawn-
/// detect which session copilot took the lock for, register host
/// ownership with the agent, then drop into the same SSE handoff +
/// take-back loop as RunSessionLoopAsync. If detection times out, we
/// just let copilot run unsupervised (no coordination, like the old
/// behaviour) so the user isn't blocked.
/// </summary>
static async Task<int> RunSessionLoopWithDetectionAsync(AgentClient agent, WrapperOptions opts)
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

    PtyHost copilotHost;
    try
    {
        copilotHost = await PtyHost.SpawnAsync(copilotPath, opts.ForwardArgs, Environment.CurrentDirectory);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"magpilot: failed to spawn copilot in PTY: {ex.Message}");
        agent.Dispose();
        return 4;
    }

    await using (copilotHost)
    {
        // Background-detect which session copilot ended up taking, then
        // register HostOwnership with the agent. We use the copilot
        // child's PID as the host PID so the agent's liveness sweep
        // doesn't prune the entry the moment our launcher dies (which
        // it doesn't here, but matches the claim semantics: the
        // wrapper PID and the copilot PID are functionally equivalent
        // from the agent's POV, the sweep only cares that *something*
        // alive owns the session). Once registration completes, we
        // start listening for release_requested SSE events.
        using var sseCts = new CancellationTokenSource();
        var preempted = new TaskCompletionSource<ReleaseRequested>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? detectedSid = null;
        _ = Task.Run(async () =>
        {
            try
            {
                detectedSid = await PostSpawnDetector.WaitForSessionAsync(copilotHost.Pid, sseCts.Token);
                if (detectedSid is null)
                {
                    Console.Error.WriteLine(
                        $"\r\nmagpilot: post-spawn detection timed out. " +
                        "Session not registered for cooperative handoff; SPA may show it as Locked.\r\n");
                    return;
                }

                try
                {
                    await agent.AcquireForHostAsync(detectedSid, copilotHost.Pid, force: false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"\r\nmagpilot: acquire-for-host on detected session {detectedSid} failed: {ex.Message}\r\n");
                    return;
                }

                // Now that we know the sid, subscribe to its SSE so we
                // can react to release_requested events.
                try
                {
                    await foreach (var evt in agent.SubscribeAsync(detectedSid, sseCts.Token))
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
                    Console.Error.WriteLine($"\r\nmagpilot: SSE subscribe failed: {ex.Message}\r\n");
                }
            }
            catch (OperationCanceledException) { }
        });

        var done = await Task.WhenAny(copilotHost.ExitTask, preempted.Task);
        sseCts.Cancel();

        if (done == copilotHost.ExitTask)
        {
            var exit = await copilotHost.ExitTask;
            if (detectedSid is not null)
            {
                try { await agent.ReleaseAsync(detectedSid, copilotHost.Pid); }
                catch (Exception ex) { Console.Error.WriteLine($"magpilot: release failed: {ex.Message}"); }
            }
            agent.Dispose();
            return exit;
        }

        // SSE preempt arrived first. Detection necessarily completed
        // (otherwise we couldn't have subscribed); detectedSid is non-null.
        var rrEvt = await preempted.Task;
        await copilotHost.ShutdownGracefullyAsync(TimeSpan.FromSeconds(rrEvt.Force ? 1 : 3));
        Console.Out.Write("\r\n--- web took over this session ---\r\n");
        Console.Out.Write($"   requester: {rrEvt.Requester}{(rrEvt.Force ? " (force)" : "")}\r\n");
        Console.Out.Flush();

        if (detectedSid is not null)
        {
            try { await agent.ReleaseAsync(detectedSid, copilotHost.Pid); }
            catch (Exception ex) { Console.Error.WriteLine($"magpilot: release failed: {ex.Message}"); }
        }
    }

    // The detection-path doesn't support the post-handoff "press enter
    // to take it back" loop -- the user's original argv (e.g. --continue)
    // may not be re-runnable, and we'd risk landing in a different
    // session. Exit cleanly.
    agent.Dispose();
    return 0;
}
