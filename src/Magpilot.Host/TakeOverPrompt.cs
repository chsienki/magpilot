using Magpilot.Shared.Models;

namespace Magpilot.Host;

/// <summary>
/// Renders the take-over prompt and reads the user's choice. The four
/// behaviors map to the four keys: Y (polite), n (exit), f (force), d
/// (details preview).
/// </summary>
public static class TakeOverPrompt
{
    public enum Choice { Yes, No, Force, Details }

    /// <summary>
    /// Show the prompt for an agent-owned session and read a Y/n/f/d
    /// answer. Honors <see cref="WrapperOptions"/> auto-flags before
    /// touching the terminal -- if the user passed --magpilot-take /
    /// --magpilot-force / --magpilot-no-take we just return the matching
    /// choice without printing anything. If neither and the input is
    /// non-TTY (piped, CI), throws <see cref="InvalidOperationException"/>
    /// with a clear message.
    /// </summary>
    public static Choice Ask(SessionStateInfo state, WrapperOptions opts)
    {
        if (opts.NoTake)  return Choice.No;
        if (opts.Force)   return Choice.Force;
        if (opts.Take)    return Choice.Yes;

        if (Console.IsInputRedirected)
            throw new InvalidOperationException(
                "Session is owned and stdin is not a TTY. " +
                "Pass --magpilot-take or --magpilot-force to override, " +
                "or --magpilot-no-take to fail fast.");

        Render(state);

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null) return Choice.No;
            switch (line.Trim().ToLowerInvariant())
            {
                case "":
                case "y":
                case "yes":     return Choice.Yes;
                case "n":
                case "no":      return Choice.No;
                case "f":
                case "force":   return Choice.Force;
                case "d":
                case "details": Choice.Details.ToString(); return Choice.Details; // caller re-prompts after dumping
                default:        Console.WriteLine("Please answer Y / n / f / d."); break;
            }
        }
    }

    public static void Render(SessionStateInfo state)
    {
        Console.WriteLine();
        Console.WriteLine($"Session {state.Info.Id}{(string.IsNullOrEmpty(state.Info.Summary) ? "" : "  \"" + state.Info.Summary + "\"")}");
        if (!string.IsNullOrEmpty(state.Info.Cwd))
            Console.WriteLine($"  cwd:     {state.Info.Cwd}");
        var driverLine = DescribeDriver(state);
        Console.WriteLine($"  driver:  {driverLine}");
        if (state.LastEvent is { } last && !string.IsNullOrEmpty(last.Type))
            Console.WriteLine($"  latest:  {last.Type}{(last.Timestamp is { } t ? $" at {t.LocalDateTime:HH:mm:ss}" : "")}");
        Console.WriteLine();
        Console.WriteLine("Take over? [Y]es (wait for turn) / [n]o / [f]orce now / [d]etails");
    }

    public static string DescribeDriver(SessionStateInfo state) => state.Owner switch
    {
        SessionOwner.Agent =>
            state.InFlight is { } inf
                ? $"agent ({inf.Driver ?? "unknown"}, mid-turn {DescribeAgo(inf.StartedAtMs)})"
                : "agent (idle)",
        SessionOwner.Host => $"another magpilot launcher (PID {state.HostPid})",
        SessionOwner.External => $"another terminal (PID {state.Info.OwnerPid})",
        _ => "no-one",
    };

    private static string DescribeAgo(long unixMs)
    {
        var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (ago.TotalSeconds < 10) return "just now";
        if (ago.TotalSeconds < 60) return $"{(int)ago.TotalSeconds}s";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m";
        return $"{(int)ago.TotalHours}h";
    }
}
