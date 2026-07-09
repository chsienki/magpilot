using System.Runtime.InteropServices;
using System.Text;

namespace Magpilot.Host;

/// <summary>
/// Queries the real terminal for its background colour via OSC 11 and
/// returns what it reports. copilot does the same probe internally, but
/// under a ConPTY that query never reaches the outer terminal, so copilot
/// times out and mis-themes. Running the probe here -- in magpilot, which
/// talks to the real terminal directly -- recovers the answer, which we
/// then hand to copilot via <c>COLORFGBG</c>.
///
/// <para>Must be called with the console already in raw mode (so the
/// terminal's reply arrives as immediate, unbuffered input) and before the
/// PTY I/O pumps start (so nothing else is competing for stdin).</para>
/// </summary>
internal static class TerminalBackgroundProbe
{
    /// <summary>
    /// Write the OSC 11 query and read the reply, up to <paramref name="timeout"/>.
    /// Returns the terminal's background colour, or null if input/output is
    /// redirected, the terminal doesn't answer, or the reply can't be parsed.
    /// </summary>
    public static Rgb? DetectBackground(TimeSpan timeout)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return null;

        try
        {
            var stdout = Console.OpenStandardOutput();
            var query = Encoding.ASCII.GetBytes(TerminalColor.BackgroundQuery);
            stdout.Write(query, 0, query.Length);
            stdout.Flush();

            var reply = OperatingSystem.IsWindows()
                ? ReadReplyWindows(timeout)
                : ReadReplyUnix(timeout);

            return reply is not null && TerminalColor.TryParseOscColorReply(reply, out var rgb)
                ? rgb
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Accumulated bytes look like a complete OSC colour reply once we've seen
    // a terminator (BEL or ST) after an "rgb:" payload.
    private static bool LooksComplete(string s) =>
        s.Contains("rgb:", StringComparison.OrdinalIgnoreCase) &&
        (s.Contains('\a') || s.Contains("\x1b\\"));

    // --------------- Windows ----------------
    //
    // When the terminal supports OSC 11 the reply lands within a few ms, so
    // the happy path finishes fast. When it does NOT, no input is ever
    // queued: GetNumberOfConsoleInputEvents stays 0, we never issue a
    // (blocking) ReadFile, and the deadline elapses cleanly with no orphaned
    // read to fight the input pump later.

    private const int STD_INPUT_HANDLE = -10;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNumberOfConsoleInputEvents(nint hConsoleInput, out uint lpcNumberOfEvents);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(nint hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, nint lpOverlapped);

    private static string? ReadReplyWindows(TimeSpan timeout)
    {
        var hIn = GetStdHandle(STD_INPUT_HANDLE);
        if (hIn == 0 || hIn == -1) return null;

        var acc = new StringBuilder();
        var buf = new byte[256];
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (!GetNumberOfConsoleInputEvents(hIn, out var pending) || pending == 0)
            {
                Thread.Sleep(2);
                continue;
            }
            if (!ReadFile(hIn, buf, (uint)buf.Length, out var read, 0) || read == 0)
                break;
            acc.Append(Encoding.Latin1.GetString(buf, 0, (int)read));
            if (LooksComplete(acc.ToString())) break;
        }

        return acc.Length > 0 ? acc.ToString() : null;
    }

    // --------------- Unix (Linux/macOS) ----------------
    //
    // poll() on stdin with the remaining budget, then a single read() of
    // whatever's available. Loops until a terminator or the deadline. The
    // interactive local client is a Windows story today; this keeps the
    // probe honest if it's ever run from a Unix terminal.

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nint count);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    private const short POLLIN = 0x001;

    private static string? ReadReplyUnix(TimeSpan timeout)
    {
        var acc = new StringBuilder();
        var buf = new byte[256];
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            var remaining = (int)(deadline - Environment.TickCount64);
            var fds = new[] { new PollFd { fd = 0, events = POLLIN } };
            var ready = poll(fds, 1, remaining);
            if (ready <= 0) break;
            if ((fds[0].revents & POLLIN) == 0) break;
            var n = (int)read(0, buf, buf.Length);
            if (n <= 0) break;
            acc.Append(Encoding.Latin1.GetString(buf, 0, n));
            if (LooksComplete(acc.ToString())) break;
        }

        return acc.Length > 0 ? acc.ToString() : null;
    }
}
