using Pty.Net;

namespace Magpilot.Host;

/// <summary>
/// Spawns the real copilot binary inside a pseudo-terminal and bridges
/// it to the user's terminal. Bytes flow:
///   user keystrokes -> Console.OpenStandardInput -> PTY.WriterStream -> copilot
///   copilot output  -> PTY.ReaderStream         -> Console.OpenStandardOutput -> user
/// Window resizes are propagated via PTY.Resize.
///
/// On preemption (the wrapper's SSE loop sees a release_requested event),
/// <see cref="ShutdownGracefullyAsync"/> writes "/exit\r" to copilot's
/// stdin (graceful), waits up to 3s for clean exit, then hard-kills as
/// a fallback.
/// </summary>
public sealed class PtyHost : IAsyncDisposable
{
    private readonly IPtyConnection _conn;
    private readonly RawConsoleMode _raw;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PtyHost(IPtyConnection conn, RawConsoleMode raw)
    {
        _conn = conn;
        _raw  = raw;
        _conn.ProcessExited += (_, e) =>
        {
            _exitCode.TrySetResult(e.ExitCode);
            _exited.TrySetResult();
        };
    }

    public int Pid => _conn.Pid;
    public Task<int> ExitTask => _exitCode.Task;

    /// <summary>
    /// Spawn copilot inside a fresh PTY sized to the current terminal.
    /// Caller must dispose to clean up the PTY and restore the parent
    /// terminal mode.
    /// </summary>
    public static async Task<PtyHost> SpawnAsync(string copilotPath, IReadOnlyList<string> argv, string cwd, CancellationToken ct = default)
    {
        var (cols, rows) = TryGetWindowSize();
        var options = new PtyOptions
        {
            Name = "magpilot-pty",
            Cwd = cwd,
            Cols = cols,
            Rows = rows,
            App = copilotPath,
            CommandLine = argv.ToArray(),
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TERM"] = Environment.GetEnvironmentVariable("TERM") ?? "xterm-256color",
                // Hint truecolor support so the child uses themed 24-bit RGB
                // sequences instead of falling back to ANSI 16-color bright
                // variants. Most modern terminals (Windows Terminal, alacritty,
                // iTerm, WezTerm, ...) support truecolor, but few set
                // COLORTERM explicitly -- so an app that checks env (chalk,
                // ansicolors, supports-color, etc.) sees an empty COLORTERM
                // under ConPTY and downgrades to bright-16, which the user's
                // theme then renders saturated/wrong. If the parent process
                // already has COLORTERM set we honour it; otherwise force
                // truecolor as the safe default.
                ["COLORTERM"] = Environment.GetEnvironmentVariable("COLORTERM") ?? "truecolor",
                // Pass through everything else from our process.
            },
        };
        // Mix in our env so the child inherits the user's PATH, USERPROFILE etc.
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            options.Environment[(string)kv.Key] = (string?)kv.Value ?? "";

        var conn = await PtyProvider.SpawnAsync(options, ct);

        var raw = new RawConsoleMode();
        raw.Enter();

        var host = new PtyHost(conn, raw);
        host.StartPumps();
        host.StartResizeWatcher();
        return host;
    }

    private void StartPumps()
    {
        // Output pump: PTY -> stdout
        _ = Task.Run(async () =>
        {
            var stdout = Console.OpenStandardOutput();
            var buf = new byte[4096];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await _conn.ReaderStream.ReadAsync(buf.AsMemory(), _cts.Token);
                    if (n <= 0) break;
                    await stdout.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                    await stdout.FlushAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* pty closed */ }
        });

        // Input pump: stdin -> PTY
        _ = Task.Run(async () =>
        {
            var stdin = Console.OpenStandardInput();
            var buf = new byte[1024];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await stdin.ReadAsync(buf.AsMemory(), _cts.Token);
                    if (n <= 0) break;
                    await _conn.WriterStream.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                    await _conn.WriterStream.FlushAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* pty closed */ }
        });
    }

    private void StartResizeWatcher()
    {
        // Poll for Console window-size changes every 250ms and propagate
        // to the PTY. Cheap and avoids platform-specific signal plumbing
        // (SIGWINCH on Unix, console-resize event on Windows).
        _ = Task.Run(async () =>
        {
            var (lastCols, lastRows) = TryGetWindowSize();
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(250, _cts.Token); }
                catch (OperationCanceledException) { return; }
                var (cols, rows) = TryGetWindowSize();
                if ((cols != lastCols || rows != lastRows) && cols > 0 && rows > 0)
                {
                    try { _conn.Resize(cols, rows); } catch { }
                    (lastCols, lastRows) = (cols, rows);
                }
            }
        });
    }

    private static (int cols, int rows) TryGetWindowSize()
    {
        try
        {
            var c = Console.WindowWidth;
            var r = Console.WindowHeight;
            return (c > 0 ? c : 80, r > 0 ? r : 24);
        }
        catch { return (80, 24); }
    }

    /// <summary>
    /// Polite shutdown: write "/exit\r" into copilot's stdin so its TUI
    /// runs its normal exit path. Wait <paramref name="gracePeriod"/>
    /// for clean exit; if still alive, fall back to <see cref="HardKill"/>.
    /// Returns true iff the child exited cleanly within the grace window.
    /// </summary>
    public async Task<bool> ShutdownGracefullyAsync(TimeSpan? gracePeriod = null, CancellationToken ct = default)
    {
        gracePeriod ??= TimeSpan.FromSeconds(3);
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("/exit\r");
            await _conn.WriterStream.WriteAsync(bytes, ct);
            await _conn.WriterStream.FlushAsync(ct);
        }
        catch { /* pty might already be closed */ }

        try
        {
            await _exited.Task.WaitAsync(gracePeriod.Value, ct);
            return true;
        }
        catch (TimeoutException)
        {
            HardKill();
            return false;
        }
        catch (OperationCanceledException)
        {
            HardKill();
            return false;
        }
    }

    public void HardKill()
    {
        try { _conn.Kill(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _conn.Kill(); } catch { }
        try { await _exited.Task.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        _raw.Restore();
        _cts.Dispose();
    }
}
