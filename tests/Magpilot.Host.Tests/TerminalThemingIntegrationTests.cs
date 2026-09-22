using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

/// <summary>
/// End-to-end check that <see cref="PtyHost"/> actually delivers the
/// theming env to the spawned child. Uses the explicit-background config
/// path (MAGPILOT_TERM_BACKGROUND=dark) so it doesn't depend on a real
/// terminal answering the OSC 11 probe -- the probe is skipped when output
/// is redirected (as it is under the test runner) or when the background is
/// pinned by config. Windows-only: it drives a cmd stub through ConPTY.
/// </summary>
public class TerminalThemingIntegrationTests
{
    [Fact]
    public async Task SpawnAsync_delivers_COLORFGBG_and_github_theme_to_child()
    {
        if (!OperatingSystem.IsWindows()) return; // ConPTY stub is cmd-based

        var dir = Directory.CreateTempSubdirectory("magpilot-theme-test");
        try
        {
            var outFile = Path.Combine(dir.FullName, "env.txt");
            var stub = Path.Combine(dir.FullName, "stub.cmd");
            // Record the two env vars we care about, then exit immediately.
            File.WriteAllText(stub,
                "@echo off\r\n" +
                $">\"{outFile}\" echo COLORFGBG=%COLORFGBG% GH=%COPILOT_GITHUB_THEME%\r\n");

            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var prevBg = Environment.GetEnvironmentVariable("MAGPILOT_TERM_BACKGROUND");
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_BACKGROUND", "dark");
            try
            {
                await using var host = await PtyHost.SpawnAsync(comspec, ["/c", stub], dir.FullName);
                await host.ExitTask.WaitAsync(TimeSpan.FromSeconds(15));
                // The stub writes the file as its last act; allow a beat for
                // the redirected write to flush after process exit.
                for (var i = 0; i < 20 && !File.Exists(outFile); i++)
                    await Task.Delay(50);

                var recorded = File.ReadAllText(outFile);
                Assert.Contains("COLORFGBG=15;0", recorded);
                Assert.Contains("GH=1", recorded);
            }
            finally
            {
                Environment.SetEnvironmentVariable("MAGPILOT_TERM_BACKGROUND", prevBg);
            }
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SpawnAsync_with_tui_changes_disabled_preserves_inherited_environment_and_output()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateTempSubdirectory("magpilot-no-tui-test");
        var keys = new[]
        {
            "COLORFGBG",
            "COPILOT_GITHUB_THEME",
            "MAGPILOT_TERM_BACKGROUND",
            "MAGPILOT_TERM_BANNER_TAG",
            "MAGPILOT_TERM_DUMP_POST",
            "MAGPILOT_TERM_MANIFEST",
        };
        var previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

        try
        {
            var outFile = Path.Combine(dir.FullName, "env.txt");
            var dumpFile = Path.Combine(dir.FullName, "output.txt");
            var manifestFile = Path.Combine(dir.FullName, "manifest.json");
            var stub = Path.Combine(dir.FullName, "stub.cmd");
            File.WriteAllText(stub,
                "@echo off\r\n" +
                $">\"{outFile}\" echo COLORFGBG=%COLORFGBG% GH=%COPILOT_GITHUB_THEME%\r\n" +
                "echo Copilot v1 uses AI.\r\n");

            Environment.SetEnvironmentVariable("COLORFGBG", "7;8");
            Environment.SetEnvironmentVariable("COPILOT_GITHUB_THEME", "parent");
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_BACKGROUND", "dark");
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_BANNER_TAG", " TEST-TAG");
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_DUMP_POST", dumpFile);
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_MANIFEST", manifestFile);

            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            await using (var host = await PtyHost.SpawnAsync(
                comspec,
                ["/c", stub],
                dir.FullName,
                tuiOptions: LauncherTuiOptions.None))
            {
                await host.ExitTask.WaitAsync(TimeSpan.FromSeconds(15));
                for (var i = 0; i < 20 && (!File.Exists(outFile) || !File.Exists(dumpFile)); i++)
                    await Task.Delay(50);
                await Task.Delay(100);
            }

            Assert.Contains("COLORFGBG=7;8 GH=parent", File.ReadAllText(outFile));

            string? output = null;
            for (var i = 0; i < 20 && output is null; i++)
            {
                try { output = File.ReadAllText(dumpFile); }
                catch (IOException) { await Task.Delay(50); }
            }

            Assert.NotNull(output);
            Assert.DoesNotContain("TEST-TAG", output);

            var manifest = System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllText(manifestFile),
                HostGeneralJsonContext.Default.TerminalLaunchManifest);
            Assert.NotNull(manifest);
            Assert.Equal(["none"], manifest.EnabledOptions);
            Assert.Equal("7;8", manifest.ColorFgBg);
            Assert.Equal("parent", manifest.CopilotGithubTheme);
            Assert.False(manifest.PaletteApplied);
            Assert.False(manifest.RewriteEnabled);
            Assert.False(manifest.ThinkingRewriteEnabled);
            Assert.False(manifest.InputBandRewriteEnabled);
            Assert.False(manifest.LegacyColorRewriteEnabled);
            Assert.False(manifest.PaletteQueryRepliesEnabled);
            Assert.False(manifest.BannerEnabled);
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SpawnAsync_applies_only_the_selected_terminal_default()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateTempSubdirectory("magpilot-tui-option-test");
        var keys = new[] { "TERM", "COLORTERM", "COLORFGBG", "COPILOT_GITHUB_THEME" };
        var previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var key in keys)
                Environment.SetEnvironmentVariable(key, null);

            var outFile = Path.Combine(dir.FullName, "env.txt");
            var stub = Path.Combine(dir.FullName, "stub.cmd");
            File.WriteAllText(stub,
                "@echo off\r\n" +
                $">\"{outFile}\" echo TERM=%TERM% CT=%COLORTERM% BG=%COLORFGBG% GH=%COPILOT_GITHUB_THEME%\r\n");

            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            await using var host = await PtyHost.SpawnAsync(
                comspec,
                ["/c", stub],
                dir.FullName,
                tuiOptions: LauncherTuiOptions.Term);
            await host.ExitTask.WaitAsync(TimeSpan.FromSeconds(15));

            for (var i = 0; i < 20 && !File.Exists(outFile); i++)
                await Task.Delay(50);

            Assert.Contains("TERM=xterm-256color CT= BG= GH=", File.ReadAllText(outFile));
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Timed_dump_captures_delayed_child_output_within_window()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateTempSubdirectory("magpilot-delayed-dump-test");
        var keys = new[] { "MAGPILOT_TERM_DUMP_POST", "MAGPILOT_TERM_DUMP_MS" };
        var previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

        try
        {
            var dumpFile = Path.Combine(dir.FullName, "output.txt");
            var stub = Path.Combine(dir.FullName, "stub.cmd");
            File.WriteAllText(stub,
                "@echo off\r\n" +
                "ping 127.0.0.1 -n 2 >nul\r\n" +
                "echo delayed output\r\n");

            Environment.SetEnvironmentVariable("MAGPILOT_TERM_DUMP_POST", dumpFile);
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_DUMP_MS", "3000");

            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            await using (var host = await PtyHost.SpawnAsync(
                comspec,
                ["/c", stub],
                dir.FullName,
                tuiOptions: LauncherTuiOptions.None))
            {
                await host.ExitTask.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(100);
            }

            string? output = null;
            for (var i = 0; i < 20 && output is null; i++)
            {
                try { output = File.ReadAllText(dumpFile); }
                catch (IOException) { await Task.Delay(50); }
            }

            Assert.Contains("delayed output", output);
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DisposeAsync_drains_final_child_output_before_returning()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateTempSubdirectory("magpilot-drain-test");
        var previousDump = Environment.GetEnvironmentVariable("MAGPILOT_TERM_DUMP_POST");

        try
        {
            var dumpFile = Path.Combine(dir.FullName, "output.txt");
            var stub = Path.Combine(dir.FullName, "stub.cmd");
            File.WriteAllText(stub,
                "@echo off\r\n" +
                "for /L %%i in (1,1,2000) do @echo line %%i\r\n" +
                "echo FINAL-TERMINAL-RESET\r\n");
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_DUMP_POST", dumpFile);

            var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            await using (var host = await PtyHost.SpawnAsync(
                comspec,
                ["/c", stub],
                dir.FullName,
                tuiOptions: LauncherTuiOptions.None))
            {
                await host.ExitTask.WaitAsync(TimeSpan.FromSeconds(15));
            }

            Assert.Contains("FINAL-TERMINAL-RESET", File.ReadAllText(dumpFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAGPILOT_TERM_DUMP_POST", previousDump);
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }
}
