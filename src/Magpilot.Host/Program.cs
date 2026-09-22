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

if (opts.ConsoleModeSnapshot is not null)
{
    return ConsoleModeSnapshot.Write(opts.ConsoleModeSnapshot);
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

if (opts.Pair is not null)
{
    return await MagpilotPair.RunAsync(opts.Pair);
}

if (opts.PairDiscover)
{
    return await MagpilotPairDiscover.RunAsync();
}

if (opts.Claim is not null)
{
    return await MagpilotClaim.RunAsync(opts.Claim);
}

// --magpilot-skip-check wins over everything: degrade to a transparent
// pass-through that just exec's the real copilot.
if (opts.SkipCheck)
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null, opts.TuiOptions);

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
catch (InvalidOperationException)
{
    // No agent token configured. Rather than erroring, run standalone: the
    // user still gets the enhanced PTY experience (terminal theming +
    // thinking-colour rewrite), just without agent session coordination.
    return await RunPassthroughAsync(opts);
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
        Console.Error.WriteLine("magpilot: running without agent coordination. Use --magpilot-skip-check to silence.");
        agent.Dispose();
        return await RunPassthroughAsync(opts);
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
        return await RunPassthroughAsync(opts);
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
// for explicit --resume=<UUID> or --session-id=<UUID> -- everything else
// (--resume="some name", --resume=<id-prefix>, --continue, no args +
// interactive picker, new session) needs the post-spawn detection path
// so we can still register HostOwnership with the agent and the SPA
// shows it as Host-owned instead of Locked.
var sid = opts.ExtractKnownSessionId();

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
        return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null, opts.TuiOptions);
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
    return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null, opts.TuiOptions);
}

if (state is null)
{
    // Session unknown to the agent (scanner hasn't picked it up yet, or
    // a fresh sid the user pre-allocated via --session-id=<uuid>). We
    // have a UUID from ExtractKnownSessionId so we know which session
    // copilot is going to load. The session directory does not exist yet, so
    // the loop acquires its terminal lease after the child creates the lock.
    return await RunSessionLoopAsync(agent, sid, opts, initialLeaseId: null);
}

// If owned by the agent or another host, prompt to take over.
Guid? leaseId = null;
if (state.Owner is SessionOwner.Agent or SessionOwner.Host or SessionOwner.External or SessionOwner.Contended)
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

    var force = choice == TakeOverPrompt.Choice.Force;
    var hostPid = Environment.ProcessId;
    Console.WriteLine($"magpilot: {(force ? "force-acquiring" : "waiting for current turn to finish")}...");
    // Politely tell any SSE subscribers (typically a SPA tab open
    // against this session) that the session is being taken over,
    // BEFORE we flip ownership on the agent. The SPA's Apply()
    // reacts to release_requested by stopping its stream and
    // showing a "Take back" affordance. Without this, the SPA only
    // discovers the takeover when its NEXT /messages POST returns
    // 409 -- i.e. the user has to send before the UI updates,
    // which feels broken from their side. Best-effort: a failed
    // broadcast doesn't block the acquire (the 409 path still
    // catches things eventually).
    try
    {
        await agent.FireReleaseRequestAsync(sid, $"magpilot/{hostPid}", force);
        // Brief grace so the SSE event has time to land + the SPA's
        // Apply() to run before the actual acquire flips ownership.
        await Task.Delay(500);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"magpilot: release-request before acquire failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
    }
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
    leaseId = state.HostLeaseId
        ?? throw new InvalidOperationException("Agent acquired the session without returning a terminal lease.");
}
else
{
    state = await agent.AcquireForHostAsync(sid, Environment.ProcessId, force: false);
    leaseId = state.HostLeaseId
        ?? throw new InvalidOperationException("Agent acquired the session without returning a terminal lease.");
}

// Spawn copilot --resume=<sid> with the user's terminal. Then watch for
// either copilot exiting on its own OR the agent firing release_requested.
return await RunSessionLoopAsync(agent, sid, opts, leaseId);


// ----------------------------------------------------------------------
// Helpers
// ----------------------------------------------------------------------

static async Task<int> RunPassthroughAsync(WrapperOptions opts)
{
    // Run copilot without agent coordination, but still through the PTY when
    // we own a real terminal -- that's what gives the enhanced experience.
    // With TUI changes disabled there is no reason to interpose a PTY, so
    // direct-exec for parity with launching raw copilot.
    var canPty = !Console.IsInputRedirected && !Console.IsOutputRedirected;
    if (!canPty || (opts.NoTuiChanges && !TerminalDiagnostics.CaptureRequested))
        return await ExecRealCopilotAsync(opts.ForwardArgs, agentClient: null, opts.TuiOptions);

    string exe; IReadOnlyList<string> argv;
    try { (exe, argv) = CopilotLaunch.Resolve(opts.ForwardArgs); }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"magpilot: {ex.Message}");
        return 127;
    }

    await using var host = await PtyHost.SpawnAsync(
        exe,
        argv,
        Environment.CurrentDirectory,
        tuiOptions: opts.TuiOptions);
    return await host.ExitTask;
}

static async Task<int> ExecRealCopilotAsync(
    IReadOnlyList<string> forwardArgs,
    AgentClient? agentClient,
    LauncherTuiOptions tuiOptions)
{
    string exe; IReadOnlyList<string> argv;
    try { (exe, argv) = CopilotLaunch.Resolve(forwardArgs); }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"magpilot: {ex.Message}");
        return 127;
    }

    var psi = new ProcessStartInfo
    {
        FileName = exe,
        UseShellExecute = false,
        // Inherit our stdin/stdout/stderr so copilot owns the TTY directly.
        RedirectStandardInput = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
    };
    foreach (var a in argv) psi.ArgumentList.Add(a);

    var resetColors = false;
    TerminalThemeConfig? theme = null;
    var needsTheme = (tuiOptions &
        (LauncherTuiOptions.Background |
         LauncherTuiOptions.GithubTheme |
         LauncherTuiOptions.Palette)) != 0;
    if (needsTheme)
    {
        // Apply terminal theming here too, so the agentless passthrough
        // (--magpilot-skip-check, or the agent-unreachable fallback) still
        // gets palette overrides + the GitHub theme flag. copilot inherits
        // the real terminal here, so its own OSC 11 background probe works.
        theme = TerminalThemeConfig.Load();
        TerminalTheming.PopulateChildEnv(
            psi.Environment!,
            theme,
            TerminalTheming.PinnedBackground(theme),
            applyBackgroundHint: tuiOptions.Includes(LauncherTuiOptions.Background),
            applyGithubTheme: tuiOptions.Includes(LauncherTuiOptions.GithubTheme));
        if (tuiOptions.Includes(LauncherTuiOptions.Palette))
            resetColors = TerminalTheming.ApplyPalette(theme);
    }

    TerminalDiagnostics.WriteManifest(
        "direct",
        tuiOptions,
        conPtyImplementation: "direct",
        columns: null,
        rows: null,
        key => psi.Environment.TryGetValue(key, out var value) ? value : null,
        theme,
        detectedBackground: null,
        resolvedIsDark: null,
        paletteApplied: resetColors,
        thinkingRewriteEnabled: false,
        inputBandRewriteEnabled: false,
        legacyColorRewriteEnabled: false,
        bannerEnabled: false,
        dumpDuration: TerminalDiagnostics.ResolveDumpDuration());

    try
    {
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exe}");
        await p.WaitForExitAsync();
        agentClient?.Dispose();
        return p.ExitCode;
    }
    finally
    {
        if (resetColors) TerminalTheming.ResetPalette();
    }
}

static async Task<int> RunSessionLoopAsync(
    AgentClient agent,
    string sid,
    WrapperOptions opts,
    Guid? initialLeaseId)
{
    var leaseId = initialLeaseId;
    IReadOnlyList<string> copilotArgs = WithResumeFlag(opts.ForwardArgs, sid);
    string exe; IReadOnlyList<string> argv;
    try { (exe, argv) = CopilotLaunch.Resolve(copilotArgs); }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"magpilot: {ex.Message}");
        await ReleaseLeaseAfterLaunchFailureAsync(agent, sid, leaseId);
        agent.Dispose();
        return 127;
    }

    var hostPid = Environment.ProcessId;

    while (true)
    {
        var handbackFailed = false;
        // Spawn copilot inside a PTY. PtyHost wires up stdin/stdout
        // pumping and puts our terminal in raw mode so copilot's TUI
        // sees keystrokes verbatim. Disposing the PtyHost restores the
        // terminal mode -- so we always wrap in await using.
        PtyHost copilotHost;
        try
        {
            copilotHost = await PtyHost.SpawnAsync(
                exe,
                argv,
                Environment.CurrentDirectory,
                tuiOptions: opts.TuiOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: failed to spawn copilot in PTY: {ex.Message}");
            await ReleaseLeaseAfterLaunchFailureAsync(agent, sid, leaseId);
            agent.Dispose();
            return 4;
        }

        await using (copilotHost)
        {
            var deferredErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            if (leaseId is null)
            {
                using var detectCts = new CancellationTokenSource();
                var detectedTask = PostSpawnDetector.WaitForSessionAsync(
                    copilotHost.Pid,
                    detectCts.Token);
                var first = await Task.WhenAny(copilotHost.ExitTask, detectedTask);
                if (first == copilotHost.ExitTask)
                {
                    detectCts.Cancel();
                    var exit = await copilotHost.ExitTask;
                    agent.Dispose();
                    return exit;
                }

                var detectedSid = await detectedTask;
                if (!string.Equals(detectedSid, sid, StringComparison.Ordinal))
                {
                    deferredErrors.Enqueue(
                        $"magpilot: could not confirm terminal ownership for {sid}; " +
                        $"detected {(detectedSid ?? "no session")} instead.");
                }
                else
                {
                    try
                    {
                        var acquired = await agent.AcquireForHostAsync(
                            sid,
                            hostPid,
                            force: false);
                        leaseId = acquired.HostLeaseId
                            ?? throw new InvalidOperationException(
                                "Agent acquired the session without returning a terminal lease.");
                    }
                    catch (Exception ex)
                    {
                        deferredErrors.Enqueue(
                            $"magpilot: post-spawn terminal acquisition failed: {ex.Message}");
                    }
                }
            }

            if (leaseId is null)
            {
                var exit = await copilotHost.ExitTask;
                FlushDeferredErrors(deferredErrors);
                agent.Dispose();
                return exit;
            }

            // Listen for release-requested in the background; signal via cts.
            // Diagnostics from the listener must be deferred -- copilot owns
            // the screen while this runs, so writing to stderr corrupts the
            // TUI. Queue and flush after copilot exits.
            using var sseCts = new CancellationTokenSource();
            var preempted = new TaskCompletionSource<ReleaseRequested>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => SubscribeWithReconnectAsync(agent, sid, preempted, deferredErrors, sseCts.Token));

            var done = await Task.WhenAny(copilotHost.ExitTask, preempted.Task);
            sseCts.Cancel();

            if (done == copilotHost.ExitTask)
            {
                // Child exited on its own. Release ownership and return.
                var exit = await copilotHost.ExitTask;
                try { await agent.ReleaseAsync(sid, leaseId.Value); }
                catch (Exception ex)
                {
                    handbackFailed = true;
                    deferredErrors.Enqueue($"magpilot: release failed: {ex.Message}");
                }
                FlushDeferredErrors(deferredErrors);
                agent.Dispose();
                return handbackFailed && exit == 0 ? 6 : exit;
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

            try
            {
                await agent.ReleaseAsync(sid, leaseId.Value);
                leaseId = null;
            }
            catch (Exception ex)
            {
                handbackFailed = true;
                deferredErrors.Enqueue($"magpilot: release failed: {ex.Message}");
            }
            FlushDeferredErrors(deferredErrors);
        }
        // PtyHost disposed here -- raw mode restored, cooked mode back.

        if (handbackFailed)
        {
            agent.Dispose();
            return 6;
        }

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
        // Tell any SSE subscriber (e.g. a SPA tab currently driving this
        // session) that we're reclaiming it BEFORE we flip ownership --
        // the same courtesy the two spawn paths extend. Without it a browser
        // actively viewing the session gets no live "terminal took over"
        // banner: it only finds out on its next /messages 409 or a manual
        // refresh's /state probe. Best-effort; a failed broadcast doesn't
        // block the take-back (the acquire still flips ownership).
        try
        {
            await agent.FireReleaseRequestAsync(sid, $"magpilot/{hostPid}", force: false);
            await Task.Delay(500);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: release-request before take-back failed (non-fatal): {ex.Message}");
        }
        try
        {
            var acquired = await agent.AcquireForHostAsync(sid, hostPid, force: false);
            leaseId = acquired.HostLeaseId
                ?? throw new InvalidOperationException(
                    "Agent acquired the session without returning a terminal lease.");
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

static async Task ReleaseLeaseAfterLaunchFailureAsync(
    AgentClient agent,
    string sessionId,
    Guid? leaseId)
{
    if (leaseId is not { } lease)
        return;

    try
    {
        await agent.ReleaseAsync(sessionId, lease);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"magpilot: restoring agent ownership after launch failure failed: {ex.Message}");
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
    string exe; IReadOnlyList<string> argv;
    try { (exe, argv) = CopilotLaunch.Resolve(opts.ForwardArgs); }
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
        copilotHost = await PtyHost.SpawnAsync(
            exe,
            argv,
            Environment.CurrentDirectory,
            tuiOptions: opts.TuiOptions);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"magpilot: failed to spawn copilot in PTY: {ex.Message}");
        agent.Dispose();
        return 4;
    }

    await using (copilotHost)
    {
        var handbackFailed = false;
        // Background-detect which session copilot ended up taking, then
        // register a terminal lease with the agent. The launcher PID is the
        // coordinator identity; the agent verifies that the detected Copilot
        // lock holder is its descendant.
        //
        // The detection task runs concurrently with copilot's TUI, so any
        // diagnostics it produces must be deferred -- writing to stderr
        // while copilot is rendering corrupts the user-visible screen.
        // Errors are queued and printed after copilot exits.
        using var sseCts = new CancellationTokenSource();
        var preempted = new TaskCompletionSource<ReleaseRequested>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferredErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        string? detectedSid = null;
        Guid? leaseId = null;
        _ = Task.Run(async () =>
        {
            try
            {
                detectedSid = await PostSpawnDetector.WaitForSessionAsync(copilotHost.Pid, sseCts.Token);
                if (detectedSid is null)
                {
                    deferredErrors.Enqueue(
                        "magpilot: post-spawn detection timed out. " +
                        "Session not registered for cooperative handoff; SPA may show it as Locked.");
                    return;
                }

                // Same release-request-before-acquire pattern as the
                // known-sid path: tell any SSE subscribers (typically a
                // SPA tab) that we're claiming the session, so they
                // shut down their stream cleanly instead of finding
                // out via 409 on the next send. Best-effort: failure
                // doesn't block the acquire.
                try
                {
                    await agent.FireReleaseRequestAsync(detectedSid, $"magpilot/{hostPid}", force: false, sseCts.Token);
                    await Task.Delay(500, sseCts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    deferredErrors.Enqueue($"magpilot: release-request before acquire failed (non-fatal): {ex.Message}");
                }

                try
                {
                    var acquired = await agent.AcquireForHostAsync(
                        detectedSid,
                        hostPid,
                        force: false);
                    leaseId = acquired.HostLeaseId
                        ?? throw new InvalidOperationException(
                            "Agent acquired the session without returning a terminal lease.");
                }
                catch (Exception ex)
                {
                    deferredErrors.Enqueue($"magpilot: acquire-for-host on detected session {detectedSid} failed: {ex.Message}");
                    return;
                }

                // Now that we know the sid, subscribe to its SSE so we
                // can react to release_requested events -- reconnecting
                // across agent restarts (see SubscribeWithReconnectAsync).
                await SubscribeWithReconnectAsync(agent, detectedSid, preempted, deferredErrors, sseCts.Token);
            }
            catch (OperationCanceledException) { }
        });

        var done = await Task.WhenAny(copilotHost.ExitTask, preempted.Task);
        sseCts.Cancel();

        if (done == copilotHost.ExitTask)
        {
            var exit = await copilotHost.ExitTask;
            if (detectedSid is not null && leaseId is { } exitLease)
            {
                try { await agent.ReleaseAsync(detectedSid, exitLease); }
                catch (Exception ex)
                {
                    handbackFailed = true;
                    deferredErrors.Enqueue($"magpilot: release failed: {ex.Message}");
                }
            }
            FlushDeferredErrors(deferredErrors);
            agent.Dispose();
            return handbackFailed && exit == 0 ? 6 : exit;
        }

        // SSE preempt arrived first. Detection necessarily completed
        // (otherwise we couldn't have subscribed); detectedSid is non-null.
        var rrEvt = await preempted.Task;
        await copilotHost.ShutdownGracefullyAsync(TimeSpan.FromSeconds(rrEvt.Force ? 1 : 3));
        Console.Out.Write("\r\n--- web took over this session ---\r\n");
        Console.Out.Write($"   requester: {rrEvt.Requester}{(rrEvt.Force ? " (force)" : "")}\r\n");
        Console.Out.Flush();

        if (detectedSid is not null && leaseId is { } handoffLease)
        {
            try { await agent.ReleaseAsync(detectedSid, handoffLease); }
            catch (Exception ex)
            {
                handbackFailed = true;
                deferredErrors.Enqueue($"magpilot: release failed: {ex.Message}");
            }
        }
        FlushDeferredErrors(deferredErrors);
        if (handbackFailed)
        {
            agent.Dispose();
            return 6;
        }
    }

    // The detection-path doesn't support the post-handoff "press enter
    // to take it back" loop -- the user's original argv (e.g. --continue)
    // may not be re-runnable, and we'd risk landing in a different
    // session. Exit cleanly.
    agent.Dispose();
    return 0;
}

static void FlushDeferredErrors(System.Collections.Concurrent.ConcurrentQueue<string> queue)
{
    while (queue.TryDequeue(out var msg))
        Console.Error.WriteLine(msg);
}

/// <summary>
/// Subscribe to a session's release_requested SSE stream, reconnecting
/// with backoff until copilot exits (<paramref name="ct"/>) or a
/// <see cref="ReleaseRequested"/> arrives (completing
/// <paramref name="preempted"/>).
///
/// The launcher registers host ownership once, at spawn, and never
/// re-asserts it. When the agent restarts -- which happens on every
/// re-pair and every update -- the SSE stream drops. A single
/// await-foreach would end there, leaving the launcher deaf to any later
/// web take-over: the SPA's cooperative release_requested would go
/// unheard, its handoff would time out, and it would fall back to a force
/// kill. Reconnecting keeps a long-lived interactive session cooperatively
/// preemptible across agent restarts. Diagnostics are deferred (copilot
/// owns the screen) and the drop is logged once to avoid spamming.
/// </summary>
static async Task SubscribeWithReconnectAsync(
    AgentClient agent,
    string sid,
    TaskCompletionSource<ReleaseRequested> preempted,
    System.Collections.Concurrent.ConcurrentQueue<string> deferredErrors,
    CancellationToken ct)
{
    var backoff = TimeSpan.FromSeconds(1);
    var maxBackoff = TimeSpan.FromSeconds(15);
    var loggedDrop = false;

    while (!ct.IsCancellationRequested)
    {
        try
        {
            await foreach (var evt in agent.SubscribeAsync(sid, ct))
            {
                if (evt is ReleaseRequested rr)
                {
                    preempted.TrySetResult(rr);
                    return;
                }
            }
            // Stream ended without error (agent closed it, e.g. shutting
            // down for a restart). Fall through to reconnect.
        }
        // Only OUR cancellation token means "stop for real" -- copilot exited
        // or the session was handed off (sseCts.Cancel()). Any OTHER
        // OperationCanceledException is a transient fault to retry, NOT a
        // shutdown. The one that bit us: HttpClient.Timeout firing on
        // SubscribeAsync's header fetch while the agent is mid-restart (its
        // AcpStarter blocks Kestrel ~30-45s, longer than the client timeout).
        // That surfaces as a TaskCanceledException whose token is the internal
        // timeout, not ct -- so the old blanket `catch (OperationCanceledException)`
        // mistook it for a real cancel, returned, and left the launcher
        // permanently deaf to release_requested after the restart (a later web
        // take-over then had to force-kill instead of handing off cleanly).
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (!loggedDrop)
            {
                deferredErrors.Enqueue($"magpilot: SSE stream dropped ({ex.Message}); reconnecting in background...");
                loggedDrop = true;
            }
        }

        if (ct.IsCancellationRequested) return;
        try { await Task.Delay(backoff, ct); }
        catch (OperationCanceledException) { return; }
        backoff = TimeSpan.FromMilliseconds(Math.Min(maxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
    }
}
