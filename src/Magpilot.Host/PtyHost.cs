using Magpilot.Shared;
using Porta.Pty;

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
    private readonly TerminalPaletteQueryResponder? _paletteQueryResponder;
    private readonly AnsiColorRewriter? _rewriter;
    private readonly BannerTagInjector? _banner;
    private readonly TimeSpan? _dumpDuration;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exitCode = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _outputPump = Task.CompletedTask;

    private PtyHost(
        IPtyConnection conn,
        RawConsoleMode raw,
        bool resetColorsOnDispose,
        TerminalPaletteQueryResponder? paletteQueryResponder,
        AnsiColorRewriter? rewriter,
        BannerTagInjector? banner,
        TimeSpan? dumpDuration)
    {
        _conn = conn;
        _raw  = raw;
        _resetColorsOnDispose = resetColorsOnDispose;
        _paletteQueryResponder = paletteQueryResponder;
        _rewriter = rewriter;
        _banner = banner;
        _dumpDuration = dumpDuration;
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
    public static async Task<PtyHost> SpawnAsync(
        string copilotPath,
        IReadOnlyList<string> argv,
        string cwd,
        LauncherTuiOptions tuiOptions = LauncherTuiOptions.All,
        CancellationToken ct = default)
    {
        var conPtyImplementation = ConPtySelector.Configure();
        var (cols, rows) = TryGetWindowSize();
        var dumpDuration = TerminalDiagnostics.ResolveDumpDuration();
        var needsTheme = (tuiOptions &
            (LauncherTuiOptions.Background |
             LauncherTuiOptions.GithubTheme |
             LauncherTuiOptions.Palette |
             LauncherTuiOptions.Rewrite |
             LauncherTuiOptions.Banner)) != 0;
        var theme = needsTheme ? TerminalThemeConfig.Load() : null;

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
        var resetColorsOnDispose = false;
        TerminalPaletteQueryResponder? paletteQueryResponder = null;
        AnsiColorRewriter? rewriter = null;
        BannerTagInjector? banner = null;
        Rgb? thinking = null;
        Rgb? inputBand = null;
        var legacyColors = false;
        Rgb? detectedBackground = null;
        bool? isDark = null;

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tuiOptions.Includes(LauncherTuiOptions.Term))
            env["TERM"] = Environment.GetEnvironmentVariable("TERM") ?? "xterm-256color";
        if (tuiOptions.Includes(LauncherTuiOptions.TrueColor))
        {
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
            env["COLORTERM"] = Environment.GetEnvironmentVariable("COLORTERM") ?? "truecolor";
        }

        // Mix in our env so the child inherits the user's PATH, USERPROFILE etc.
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            env[(string)kv.Key] = (string?)kv.Value ?? "";

        if (theme is not null)
        {
            if (tuiOptions.Includes(LauncherTuiOptions.Background))
            {
                isDark = TerminalTheming.PinnedBackground(theme)
                    ?? ((detectedBackground =
                            TerminalBackgroundProbe.DetectBackground(TimeSpan.FromMilliseconds(250))) is { } bg
                            ? TerminalColor.IsDark(bg)
                            : null);
            }

            // Apply any configured palette overrides to the real terminal.
            // Reset on dispose so we never leave the terminal recoloured.
            if (tuiOptions.Includes(LauncherTuiOptions.Palette))
                resetColorsOnDispose = TerminalTheming.ApplyPalette(theme);

            // Our computed hints win over inherited env: the probe reflects
            // the actual terminal, and the GitHub-theme flag exposes that
            // colour mode in copilot's own /theme picker.
            TerminalTheming.PopulateChildEnv(
                env,
                theme,
                isDark,
                applyBackgroundHint: tuiOptions.Includes(LauncherTuiOptions.Background),
                applyGithubTheme: tuiOptions.Includes(LauncherTuiOptions.GithubTheme));

            // Theme-file-only rewrites for output that cannot be controlled
            // through the terminal palette.
            thinking = tuiOptions.Includes(LauncherTuiOptions.Thinking)
                ? theme.Thinking
                : null;
            inputBand = tuiOptions.Includes(LauncherTuiOptions.InputBand)
                ? theme.InputBand
                : null;
            paletteQueryResponder = tuiOptions.Includes(LauncherTuiOptions.Palette) &&
                theme.HasPaletteOverrides
                ? new TerminalPaletteQueryResponder(
                    theme.Palette,
                    theme.Foreground,
                    theme.BackgroundColor)
                : null;
            legacyColors = tuiOptions.Includes(LauncherTuiOptions.LegacyColors) &&
                theme.LegacyDefaultColors;
            rewriter = thinking is not null || inputBand is not null || legacyColors
                ? new AnsiColorRewriter(thinking, inputBand, legacyColors)
                : null;

            // Brand copilot's startup banner with the Magpilot version.
            // Compatibility mode disables this because the welcome card
            // animation cannot account for bytes inserted after layout.
            banner = tuiOptions.Includes(LauncherTuiOptions.Banner) &&
                !theme.LegacyDefaultColors &&
                ResolveBannerTag() is { } tag
                ? new BannerTagInjector(tag)
                : null;
        }

        TerminalDiagnostics.WriteManifest(
            "pty",
            tuiOptions,
            conPtyImplementation,
            cols,
            rows,
            key => env.TryGetValue(key, out var value) ? value : null,
            theme,
            detectedBackground,
            isDark,
            resetColorsOnDispose,
            thinking is not null,
            inputBand is not null,
            legacyColors,
            paletteQueryResponder is not null,
            banner is not null,
            dumpDuration);

        var options = new PtyOptions
        {
            Name = "magpilot-pty",
            Cwd = cwd,
            Cols = cols,
            Rows = rows,
            App = copilotPath,
            CommandLine = argv.ToArray(),
            Environment = env,
            // Porta.Pty closes the pseudoconsole before raising ProcessExited
            // on this path, allowing the output stream to drain buffered
            // terminal reset sequences through EOF.
            UseAsyncIo = OperatingSystem.IsWindows(),
        };

        var conn = await PtyProvider.SpawnAsync(options, ct);

        var host = new PtyHost(
            conn,
            raw,
            resetColorsOnDispose,
            paletteQueryResponder,
            rewriter,
            banner,
            dumpDuration);
        host.StartPumps();
        host.StartResizeWatcher();
        return host;
    }

    // Resolves the tag appended to copilot's startup banner ("... uses AI.").
    // Default (unset env) brands the session with the magpilot version. A
    // custom MAGPILOT_TERM_BANNER_TAG value is inserted verbatim (so the caller
    // controls spacing); "0"/"off"/"false"/"none"/"no" or empty suppresses it.
    private static string? ResolveBannerTag()
    {
        var raw = Environment.GetEnvironmentVariable("MAGPILOT_TERM_BANNER_TAG");
        if (raw is null)
            return $" (Magpilot v{Versioning.AssemblyVersion})";
        var t = raw.Trim().ToLowerInvariant();
        if (t.Length == 0 || t is "0" or "off" or "false" or "none" or "no")
            return null;
        return raw;
    }

    private void StartPumps()
    {
        // Output pump: PTY -> stdout
        _outputPump = Task.Run(async () =>
        {
            var stdout = Console.OpenStandardOutput();
            var buf = new byte[4096];
            // Diagnostics: MAGPILOT_TERM_DUMP tees copilot's RAW output
            // (pre-rewrite); MAGPILOT_TERM_DUMP_POST tees what actually
            // reaches the terminal (post-rewrite). For inspecting escape
            // sequences when tuning the theme.
            Stream? dump = null, dumpPost = null;
            System.Diagnostics.Stopwatch? dumpTimer = null;
            var dumpPath = Environment.GetEnvironmentVariable("MAGPILOT_TERM_DUMP");
            if (!string.IsNullOrEmpty(dumpPath))
                try { dump = File.Create(dumpPath); } catch { /* diagnostics are best-effort */ }
            var dumpPostPath = Environment.GetEnvironmentVariable("MAGPILOT_TERM_DUMP_POST");
            if (!string.IsNullOrEmpty(dumpPostPath))
                try { dumpPost = File.Create(dumpPostPath); } catch { /* diagnostics are best-effort */ }

            async ValueTask CloseDumpsAsync()
            {
                if (dump is not null)
                {
                    await dump.DisposeAsync();
                    dump = null;
                }
                if (dumpPost is not null)
                {
                    await dumpPost.DisposeAsync();
                    dumpPost = null;
                }
            }

            async ValueTask WriteVisibleOutputAsync(
                ReadOnlyMemory<byte> output,
                CancellationToken cancellationToken)
            {
                if (_paletteQueryResponder is not null)
                {
                    var transformed = _paletteQueryResponder.Transform(output.Span);
                    output = transformed.Output;
                    if (transformed.Reply.Length > 0)
                        await WriteInputAsync(transformed.Reply, cancellationToken);
                }
                if (_banner is not null)
                    output = _banner.Transform(output.Span);
                if (_rewriter is not null)
                    output = _rewriter.Transform(output.Span);
                if (dumpPost is not null)
                {
                    await dumpPost.WriteAsync(output, cancellationToken);
                    await dumpPost.FlushAsync(cancellationToken);
                }
                if (output.Length > 0)
                {
                    await stdout.WriteAsync(output, cancellationToken);
                    await stdout.FlushAsync(cancellationToken);
                }
            }

            try
            {
                while (true)
                {
                    var n = await _conn.ReaderStream.ReadAsync(buf.AsMemory());
                    if (n <= 0) break;
                    if (_dumpDuration is { } duration)
                    {
                        dumpTimer ??= System.Diagnostics.Stopwatch.StartNew();
                        if (dumpTimer.Elapsed >= duration)
                            await CloseDumpsAsync();
                    }
                    if (dump is not null)
                    {
                        await dump.WriteAsync(buf.AsMemory(0, n));
                        await dump.FlushAsync();
                    }

                    ReadOnlyMemory<byte> outMem = buf.AsMemory(0, n);
                    await WriteVisibleOutputAsync(outMem, CancellationToken.None);
                }
            }
            catch (Exception) { /* pty closed */ }
            finally
            {
                await CloseDumpsAsync();
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
                    await WriteInputAsync(buf.AsMemory(0, n), _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* pty closed */ }
        });
    }

    private async ValueTask WriteInputAsync(
        ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken)
    {
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            await _conn.WriterStream.WriteAsync(input, cancellationToken);
            await _conn.WriterStream.FlushAsync(cancellationToken);
        }
        finally
        {
            _inputGate.Release();
        }
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
            await WriteInputAsync(bytes, ct);
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
        if (!_exited.Task.IsCompleted)
        {
            try { _conn.Kill(); } catch { }
            try { await _exited.Task.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        try { await _outputPump.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { _conn.Dispose(); } catch { }
        // Undo any palette overrides we pushed to the real terminal so the
        // user's shell isn't left recoloured after copilot exits.
        if (_resetColorsOnDispose)
            TerminalTheming.ResetPalette();
        _raw.Restore();
        _inputGate.Dispose();
        _cts.Dispose();
    }
}
