using System.Diagnostics;
using Magpilot.Agent.Sessions;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Exercises the process-ancestry walk that lets the agent reconstruct
/// host ownership after a restart. A launcher-driven copilot is a
/// descendant of the <c>magpilot</c> launcher, so walking up from a
/// session's lock PID and finding that ancestor is what re-marks the
/// session Host-owned (instead of leaving it "kill to unlock").
///
/// Windows-only: the walk uses a Toolhelp snapshot and the launcher +
/// agency only run on Windows. Tests no-op elsewhere.
/// </summary>
public sealed class ProcessAncestryTests
{
    [Fact]
    public void Finds_parent_process_as_ancestor()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var child = SpawnSleeper();
        try
        {
            var selfName = Process.GetCurrentProcess().ProcessName; // this test host
            Assert.True(
                ProcessAncestry.TryFindAncestorPidByName(child.Id, selfName, out var ancestorPid),
                "expected the spawning test process to be found as an ancestor of the child");
            Assert.Equal(Environment.ProcessId, ancestorPid);
        }
        finally
        {
            Kill(child);
        }
    }

    [Fact]
    public void Unknown_ancestor_name_is_not_found()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var child = SpawnSleeper();
        try
        {
            Assert.False(
                ProcessAncestry.TryFindAncestorPidByName(child.Id, "magpilot-no-such-ancestor-xyz", out var pid));
            Assert.Equal(0, pid);
        }
        finally
        {
            Kill(child);
        }
    }

    [Fact]
    public void Missing_start_pid_returns_false()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A PID that (almost certainly) isn't in the snapshot: the walk has
        // no starting node, so it can't find any ancestor.
        Assert.False(ProcessAncestry.TryFindAncestorPidByName(2147483646, "magpilot", out _));
    }

    private static Process SpawnSleeper() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }
}
