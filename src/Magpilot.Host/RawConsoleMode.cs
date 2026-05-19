using System.Runtime.InteropServices;

namespace Magpilot.Host;

/// <summary>
/// Toggles the parent terminal between cooked (line-buffered, echoing,
/// signal-processing) mode and raw (every byte forwarded as-is) mode.
/// Required when bridging the user's terminal to a PTY-spawned child
/// so the child's TUI sees keystrokes verbatim and the parent's stdout
/// passes through ANSI escape sequences unchanged.
///
/// Windows: SetConsoleMode flips on ENABLE_VIRTUAL_TERMINAL_*, off
/// ENABLE_LINE_INPUT/ECHO_INPUT/PROCESSED_INPUT.
/// Unix: tcsetattr with cfmakeraw().
/// </summary>
public sealed class RawConsoleMode : IDisposable
{
    private readonly bool _isWindows;
    private uint _origStdinMode, _origStdoutMode;
    private nint _stdinHandle, _stdoutHandle;
    private uint _origConsoleCp, _origConsoleOutputCp;
    private object? _origTermios;

    public RawConsoleMode()
    {
        _isWindows = OperatingSystem.IsWindows();
    }

    /// <summary>Capture current console mode and switch to raw.</summary>
    public void Enter()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return;

        if (_isWindows) EnterWindows();
        else EnterUnix();
    }

    /// <summary>Restore the captured console mode. Idempotent.</summary>
    public void Restore()
    {
        try
        {
            if (_isWindows) RestoreWindows();
            else RestoreUnix();
        }
        catch { /* best-effort on shutdown */ }
    }

    public void Dispose() => Restore();

    // --------------- Windows ----------------

    private const int STD_INPUT_HANDLE  = -10;
    private const int STD_OUTPUT_HANDLE = -11;

    private const uint ENABLE_PROCESSED_INPUT       = 0x0001;
    private const uint ENABLE_LINE_INPUT            = 0x0002;
    private const uint ENABLE_ECHO_INPUT            = 0x0004;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    private const uint ENABLE_PROCESSED_OUTPUT            = 0x0001;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private const uint DISABLE_NEWLINE_AUTO_RETURN        = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCP(uint wCodePageID);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleOutputCP(uint wCodePageID);

    private const uint CP_UTF8 = 65001;

    private void EnterWindows()
    {
        _stdinHandle  = GetStdHandle(STD_INPUT_HANDLE);
        _stdoutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetConsoleMode(_stdinHandle, out _origStdinMode))
        {
            var raw = _origStdinMode;
            raw &= ~(ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT | ENABLE_ECHO_INPUT);
            raw |= ENABLE_VIRTUAL_TERMINAL_INPUT;
            SetConsoleMode(_stdinHandle, raw);
        }
        if (GetConsoleMode(_stdoutHandle, out _origStdoutMode))
        {
            var raw = _origStdoutMode;
            raw |= ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN;
            SetConsoleMode(_stdoutHandle, raw);
        }

        // Force the console code page to UTF-8 so the bytes copilot's TUI
        // emits (box-drawing characters, bullets, em-dashes, etc.) are
        // rendered correctly. Without this, a default cmd.exe / pwsh inherits
        // CP-437 / CP-1252 and each 3-byte UTF-8 sequence shows up as garbled
        // "Gamma-o-acute" goo. Restored on exit so we don't pollute the
        // parent shell.
        _origConsoleCp       = GetConsoleCP();
        _origConsoleOutputCp = GetConsoleOutputCP();
        if (_origConsoleCp       != CP_UTF8) SetConsoleCP(CP_UTF8);
        if (_origConsoleOutputCp != CP_UTF8) SetConsoleOutputCP(CP_UTF8);
    }

    private void RestoreWindows()
    {
        if (_origStdinMode  != 0 && _stdinHandle  != 0) SetConsoleMode(_stdinHandle,  _origStdinMode);
        if (_origStdoutMode != 0 && _stdoutHandle != 0) SetConsoleMode(_stdoutHandle, _origStdoutMode);
        if (_origConsoleCp       != 0 && _origConsoleCp       != CP_UTF8) SetConsoleCP(_origConsoleCp);
        if (_origConsoleOutputCp != 0 && _origConsoleOutputCp != CP_UTF8) SetConsoleOutputCP(_origConsoleOutputCp);
    }

    // --------------- Unix (Linux/macOS) ----------------

    private const int STDIN_FD       = 0;
    private const int TCSANOW        = 0;

    [DllImport("libc")] private static extern int tcgetattr(int fd, [Out] byte[] termios_p);
    [DllImport("libc")] private static extern int tcsetattr(int fd, int optional_actions, byte[] termios_p);
    [DllImport("libc")] private static extern void cfmakeraw(byte[] termios_p);

    private void EnterUnix()
    {
        // termios struct is platform-dependent in size; allocate a generous
        // 256-byte buffer and let libc populate it. We just preserve the
        // bytes verbatim and write them back on Restore.
        var orig = new byte[256];
        if (tcgetattr(STDIN_FD, orig) != 0) return;
        _origTermios = orig;
        var raw = (byte[])orig.Clone();
        cfmakeraw(raw);
        tcsetattr(STDIN_FD, TCSANOW, raw);
    }

    private void RestoreUnix()
    {
        if (_origTermios is byte[] orig)
            tcsetattr(STDIN_FD, TCSANOW, orig);
    }
}
