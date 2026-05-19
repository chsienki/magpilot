namespace Magpilot.Host;

/// <summary>
/// One-shot implementation of <c>magpilot --magpilot-claim=&lt;sid&gt;</c>.
///
/// <para>
/// Used to recover a stranded copilot session that's already running in
/// some terminal (or that the launcher's post-spawn detector missed):
/// the SPA otherwise shows it as <c>Locked</c> and offers to kill the
/// holding PID, when really we'd rather have it shown as host-owned
/// (since "us" + "another magpilot launcher" are functionally the same
/// to the cooperative-handoff machinery).
/// </para>
///
/// <para>
/// We extract the copilot PID from the session's
/// <c>inuse.&lt;pid&gt;.lock</c> file and register THAT as the host PID
/// (not the launcher's transient PID -- if we used our own, the agent's
/// 10s liveness sweep would prune it immediately after we exit). Since
/// the copilot child is still alive, the entry stays put until copilot
/// itself exits, at which point the sweep cleans it up. Functionally
/// equivalent to having spawned copilot under us, just without the
/// launcher-owned PTY.
/// </para>
///
/// <para>
/// Limitation: because we don't own the PTY, we can't drive the polite
/// shutdown when the SPA fires <c>release_requested</c>. The wrapper
/// will register ownership, but a request-to-release won't actually
/// stop copilot. This is documented in the help text; it's still better
/// than the "kill PID" prompt as a first cut.
/// </para>
/// </summary>
internal static class MagpilotClaim
{
    public static async Task<int> RunAsync(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            Console.Error.WriteLine("magpilot: --magpilot-claim needs a session id (--magpilot-claim=<sid>).");
            return 2;
        }

        var copilotPid = PostSpawnDetector.FindHolderPid(sid);
        if (copilotPid is null)
        {
            Console.Error.WriteLine(
                $"magpilot: no inuse.<pid>.lock file under ~/.copilot/session-state/{sid}/ -- " +
                "session is not currently held by any copilot. Nothing to claim.");
            return 3;
        }

        AgentClient agent;
        try { agent = new AgentClient(); }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"magpilot: {ex.Message}");
            return 2;
        }

        try
        {
            Console.WriteLine($"magpilot: claiming session {sid} for copilot PID {copilotPid.Value}...");
            var state = await agent.AcquireForHostAsync(sid, copilotPid.Value, force: false);
            Console.WriteLine($"magpilot: registered (owner={state.Owner}, hostPid={state.HostPid}).");
            Console.WriteLine("  The SPA should now show this session as Host-owned.");
            Console.WriteLine("  When you exit copilot, the agent's PID-liveness sweep will release it.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"magpilot: claim failed ({ex.GetType().Name}: {ex.Message})");
            return 4;
        }
        finally
        {
            agent.Dispose();
        }
    }
}
