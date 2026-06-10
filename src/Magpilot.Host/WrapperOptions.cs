namespace Magpilot.Host;

/// <summary>
/// Parsed view of the magpilot launcher's own command-line flags.
/// All wrapper flags are prefixed <c>--magpilot-</c> so they can never
/// collide with any present or future copilot flag, and they are
/// stripped from argv before the underlying copilot binary is invoked.
/// </summary>
public sealed record WrapperOptions(
    /// <summary>Auto-Y on the take-over prompt: polite take, waits for the in-flight turn.</summary>
    bool Take,
    /// <summary>Auto-f on the take-over prompt: aborts in-flight (ACP cancel + 2s grace) and takes immediately. Implies <see cref="Take"/>.</summary>
    bool Force,
    /// <summary>Auto-n on the take-over prompt: if owned by anything else, exit non-zero rather than prompt or take.</summary>
    bool NoTake,
    /// <summary>Skip the agent state-check entirely; exec real copilot as a transparent passthrough. Wins over every other flag.</summary>
    bool SkipCheck,
    /// <summary>On web preemption, exit immediately instead of sitting on the "press enter to take it back" prompt.</summary>
    bool ExitOnHandoff,
    /// <summary>Don't spawn copilot; print agent reachability + sessions + their owners, then exit.</summary>
    bool Status,
    /// <summary>Print wrapper-only flag help and exit.</summary>
    bool Help,
    /// <summary>Print local + agent-reported version info and exit.</summary>
    bool Version,
    /// <summary>Download and run the latest installer from GitHub Releases, then exit.</summary>
    bool Update,
    /// <summary>Pair this agent with a hub using a bundle copied from the hub's /admin/enroll page; writes magpilot.env and restarts the scheduled task.</summary>
    string? Pair,
    /// <summary>V3 interactive discovery: --magpilot-pair with no bundle argument; UDP-discover hubs on the LAN, generate a claim, open the admin's browser to approve.</summary>
    bool PairDiscover,
    /// <summary>One-shot: register an already-running copilot session as host-owned (no spawn).</summary>
    string? Claim,
    /// <summary>Argv with all <c>--magpilot-*</c> flags stripped, ready to forward to the real copilot binary.</summary>
    IReadOnlyList<string> ForwardArgs)
{
    public static WrapperOptions Parse(string[] argv)
    {
        var take = false; var force = false; var noTake = false;
        var skipCheck = false; var exitOnHandoff = false;
        var status = false; var help = false;
        var version = false; var update = false;
        string? pair = null;
        var pairDiscover = false;
        string? claim = null;
        var forward = new List<string>(argv.Length);

        foreach (var a in argv)
        {
            // Only strip exact --magpilot-<name> matches. Unknown
            // --magpilot-* flags should error rather than silently pass
            // through, since the user clearly intended them for us.
            switch (a)
            {
                case "--magpilot-take":            take = true; break;
                case "--magpilot-force":           force = true; break;
                case "--magpilot-no-take":         noTake = true; break;
                case "--magpilot-skip-check":      skipCheck = true; break;
                case "--magpilot-exit-on-handoff": exitOnHandoff = true; break;
                case "--magpilot-status":          status = true; break;
                case "--magpilot-help":            help = true; break;
                case "--magpilot-version":         version = true; break;
                case "--magpilot-update":          update = true; break;
                case "--magpilot-pair":            pairDiscover = true; break;
                default:
                    if (a.StartsWith("--magpilot-claim=", StringComparison.Ordinal))
                    {
                        claim = a["--magpilot-claim=".Length..];
                        break;
                    }
                    if (a.StartsWith("--magpilot-pair=", StringComparison.Ordinal))
                    {
                        pair = a["--magpilot-pair=".Length..];
                        break;
                    }
                    if (a.StartsWith("--magpilot-", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown wrapper flag: {a}. Try --magpilot-help.");
                    forward.Add(a);
                    break;
            }
        }

        if (force) take = true;
        if (take && noTake)
            throw new ArgumentException("Contradictory flags: --magpilot-take (or --magpilot-force) and --magpilot-no-take both set.");

        return new WrapperOptions(take, force, noTake, skipCheck, exitOnHandoff, status, help, version, update, pair, pairDiscover, claim, forward);
    }

    /// <summary>
    /// Inspect the forward-args for a <c>--resume[=&lt;sid&gt;]</c> token and
    /// extract the session id if one was specified. Returns null if no
    /// resume flag, or empty string if <c>--resume</c> with no value (which
    /// the wrapper treats as "list sessions for this cwd").
    /// </summary>
    public string? ExtractResumeSessionId()
    {
        for (var i = 0; i < ForwardArgs.Count; i++)
        {
            var a = ForwardArgs[i];
            if (a.StartsWith("--resume=", StringComparison.Ordinal))
                return a["--resume=".Length..];
            if (a == "--resume")
                return i + 1 < ForwardArgs.Count && !ForwardArgs[i + 1].StartsWith("-")
                    ? ForwardArgs[i + 1]
                    : "";
        }
        return null;
    }

    /// <summary>
    /// Inspect the forward-args for a session id that is unambiguously a
    /// UUID -- either <c>--session-id=&lt;uuid&gt;</c> (always a UUID by copilot's
    /// own contract) or <c>--resume[=&lt;value&gt;]</c> where the value parses as a
    /// UUID. Returns null otherwise (no flag, picker mode, name, id prefix).
    ///
    /// <para>
    /// Used by the launcher to skip post-spawn session detection when the
    /// user has already told us exactly which session they want.
    /// <c>copilot --resume &lt;sid&gt;</c> does not refresh
    /// <c>~/.copilot/session-state/&lt;sid&gt;/inuse.&lt;pid&gt;.lock</c> with the new
    /// process's PID, so a detector that polls for the PID match cannot
    /// see the resumed session -- but the user-supplied UUID is itself
    /// the answer the detector would otherwise compute.
    /// </para>
    /// </summary>
    public string? ExtractKnownSessionId()
    {
        for (var i = 0; i < ForwardArgs.Count; i++)
        {
            var a = ForwardArgs[i];

            string? candidate = null;
            if (a.StartsWith("--session-id=", StringComparison.Ordinal))
                candidate = a["--session-id=".Length..];
            else if (a == "--session-id" && i + 1 < ForwardArgs.Count && !ForwardArgs[i + 1].StartsWith("-"))
                candidate = ForwardArgs[i + 1];
            else if (a.StartsWith("--resume=", StringComparison.Ordinal))
                candidate = a["--resume=".Length..];
            else if (a == "--resume" && i + 1 < ForwardArgs.Count && !ForwardArgs[i + 1].StartsWith("-"))
                candidate = ForwardArgs[i + 1];

            if (!string.IsNullOrEmpty(candidate) && Guid.TryParseExact(candidate, "D", out _))
                return candidate;
        }
        return null;
    }

    public static string HelpText => """
        magpilot -- a thin wrapper around `copilot` that coordinates with magpilot-agent.

        Wrapper-only flags (all stripped before exec; everything else is forwarded to copilot):

          --magpilot-take              auto-confirm take-over (polite: waits for in-flight turn)
          --magpilot-force             auto-confirm + abort in-flight (ACP cancel + 2s grace). Implies --magpilot-take.
          --magpilot-no-take           if owned by anything else, exit non-zero (safe for scripting)
          --magpilot-skip-check        bypass the agent entirely; exec real copilot as a passthrough
          --magpilot-exit-on-handoff   on web preemption, exit immediately (no resume prompt)
          --magpilot-status            don't spawn copilot; print agent reachability + sessions + exit
          --magpilot-version           print local + agent-reported version info, then exit
          --magpilot-update            download and run the latest installer from GitHub Releases, then exit
          --magpilot-pair              interactive pairing: UDP-discover a hub on the LAN, submit a claim,
                                       open your browser to approve. The hub admin clicks "Adopt" on the
                                       resulting pending request and the launcher finishes the handshake.
          --magpilot-pair=<bundle>     non-interactive pairing: redeem a voucher bundle copied from the
                                       hub's /admin/enroll page. Bypasses UDP discovery.
          --magpilot-claim=<sid>       one-shot: register a stranded already-running copilot session
                                       as host-owned in the agent (no spawn). The agent's PID-liveness
                                       sweep handles cleanup when the copilot child eventually exits.
          --magpilot-help              print this help and exit

        Env:
          MAGPILOT_AGENT_URL           default http://127.0.0.1:5099
          MAGPILOT_AGENT_TOKEN         required for state/acquire/release ops (the bearer shared with the agent)
          MAGPILOT_REAL_COPILOT        explicit path to the real copilot binary (optional)

        Combinations:
          --magpilot-skip-check        wins over everything else
          --magpilot-take + --magpilot-no-take -> error
          --magpilot-force             implies --magpilot-take
          (none) + TTY                 interactive Y/n/f/d prompt when owned
          (none) + non-TTY             refuse with 'pass --magpilot-take or --magpilot-force to override'

        For full copilot CLI help, run: copilot --magpilot-skip-check --help
        """;
}
