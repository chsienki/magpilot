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
}
