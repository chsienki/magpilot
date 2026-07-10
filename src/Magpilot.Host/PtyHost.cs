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
    private readonly bool _resetColorsOnDispose;
    private readonly AnsiColorRewriter? _rewriter;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PtyHost(IPtyConnection conn, RawConsoleMode raw, bool resetColorsOnDispose, AnsiColorRewriter? rewriter)
    {
        _conn = conn;
        _raw  = raw;
        _resetColorsOnDispose = resetColorsOnDispose;
        _rewriter = rewriter;
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
        var theme = TerminalThemeConfig.Load();

        // Raw mode must be on before we probe the terminal (so its OSC 11
        // reply arrives unbuffered) and before the pumps start (so nothing
        // competes for stdin). It stays on for the copilot session itself.
        var raw = new RawConsoleMode();
        raw.Enter();

        // Resolve the background copilot should assume. copilot's own OSC 11
        // probe can't reach the real terminal through the ConPTY, so it times
        // out and mis-themes; we probe here (magpilot talks to the real
        // terminal directly) and pass the answer down via COLORFGBG, which
        // copilot reads as its documented dark/light fallback. An explicit
        // config value skips the probe.
        bool? isDark = TerminalTheming.PinnedBackground(theme)
            ?? (TerminalBackgroundProbe.DetectBackground(TimeSpan.FromMilliseconds(250)) is { } bg
                ? TerminalColor.IsDark(bg)
                : null);

        // Apply any configured palette overrides to the real terminal. These
        // recolour copilot's ANSI-indexed output (its base-16 "default"
        // theme); truecolor themes emit fixed RGB and are unaffected. Reset
        // on dispose so we never leave the user's terminal recoloured.
        var resetColorsOnDispose = TerminalTheming.ApplyPalette(theme);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
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
        };
        // Mix in our env so the child inherits the user's PATH, USERPROFILE etc.
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            env[(string)kv.Key] = (string?)kv.Value ?? "";

        // Our computed hints win over inherited env: the probe reflects the
        // actual terminal (more reliable than a stale inherited COLORFGBG),
        // and the github-theme flag is a deliberate opt-in that exposes
        // copilot's GitHub colour mode in its own /theme picker.
        TerminalTheming.PopulateChildEnv(env, theme, isDark);

        // If the theme configures a "thinking" colour or an "inputBand"
        // colour, rewrite copilot's output byte stream to honour them:
        // faint (SGR 2) -> the thinking colour (copilot renders reasoning
        // faint, unreadable on dark backgrounds and not palette-fixable),
        // and copilot's fixed composer-band background -> the input-band
        // colour (it's baked into copilot's theme, not the terminal palette).
        var rewriter = theme.Thinking is not null || theme.InputBand is not null
            ? new AnsiColorRewriter(theme.Thinking, theme.InputBand)
            : null;

        var options = new PtyOptions
        {
            Name = "magpilot-pty",
            Cwd = cwd,
            Cols = cols,
            Rows = rows,
            App = copilotPath,
            CommandLine = argv.ToArray(),
            Environment = env,
        };

        var conn = await PtyProvider.SpawnAsync(options, ct);

        var host = new PtyHost(conn, raw, resetColorsOnDispose, rewriter);
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
            // Diagnostics: MAGPILOT_TERM_DUMP tees copilot's RAW output
            // (pre-rewrite); MAGPILOT_TERM_DUMP_POST tees what actually
            // reaches the terminal (post-rewrite). For inspecting escape
            // sequences when tuning the theme.
            Stream? dump = null, dumpPost = null;
            var dumpPath = Environment.GetEnvironmentVariable("MAGPILOT_TERM_DUMP");
            if (!string.IsNullOrEmpty(dumpPath))
                try { dump = File.Create(dumpPath); } catch { /* diagnostics are best-effort */ }
            var dumpPostPath = Environment.GetEnvironmentVariable("MAGPILOT_TERM_DUMP_POST");
            if (!string.IsNullOrEmpty(dumpPostPath))
                try { dumpPost = File.Create(dumpPostPath); } catch { /* diagnostics are best-effort */ }
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var n = await _conn.ReaderStream.ReadAsync(buf.AsMemory(), _cts.Token);
                    if (n <= 0) break;
                    if (dump is not null)
                    {
                        await dump.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                        await dump.FlushAsync(_cts.Token);
                    }
                    ReadOnlyMemory<byte> outMem = _rewriter is not null
                        ? _rewriter.Transform(buf.AsSpan(0, n))
                        : buf.AsMemory(0, n);
                    if (dumpPost is not null)
                    {
                        await dumpPost.WriteAsync(outMem, _cts.Token);
                        await dumpPost.FlushAsync(_cts.Token);
                    }
                    await stdout.WriteAsync(outMem, _cts.Token);
                    await stdout.FlushAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* pty closed */ }
            finally
            {
                if (dump is not null) await dump.DisposeAsync();
                if (dumpPost is not null) await dumpPost.DisposeAsync();
            }
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
        // Undo any palette overrides we pushed to the real terminal so the
        // user's shell isn't left recoloured after copilot exits.
        if (_resetColorsOnDispose)
            TerminalTheming.ResetPalette();
        _raw.Restore();
        _cts.Dispose();
    }
}
