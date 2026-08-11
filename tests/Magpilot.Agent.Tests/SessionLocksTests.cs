using Magpilot.Agent.Sessions;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Covers advisory-lock inspection + reaping -- the observability/hygiene half
/// of the stale-resume hardening. The pure classification is tested with an
/// injected liveness predicate; the reaper is exercised against a real temp dir
/// using this process (alive) and an implausible pid (dead), matching the
/// HostOwnershipTests convention.
/// </summary>
public sealed class SessionLocksTests : IDisposable
{
    private const int DeadPid = 2147483646; // implausible => never a live process

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"magpilot-locks-{Guid.NewGuid():N}");

    public SessionLocksTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("inuse.1234.lock", true, 1234)]
    [InlineData("inuse.0.lock", true, 0)]
    [InlineData("inuse.abc.lock", false, 0)]
    [InlineData("inuse.1234.lock.bak", false, 0)]
    [InlineData("held.1234.lock", false, 0)]
    [InlineData("workspace.yaml", false, 0)]
    public void TryParsePid_reads_pid_from_lock_name(string name, bool expectOk, int expectPid)
    {
        var ok = SessionLocks.TryParsePid(name, out var pid);
        Assert.Equal(expectOk, ok);
        if (expectOk) Assert.Equal(expectPid, pid);
    }

    [Fact]
    public void TryParsePid_handles_full_paths()
    {
        Assert.True(SessionLocks.TryParsePid(Path.Combine("x", "y", "inuse.777.lock"), out var pid));
        Assert.Equal(777, pid);
    }

    [Fact]
    public void Inspect_classifies_holders_by_injected_liveness()
    {
        string[] files = ["inuse.10.lock", "inuse.20.lock", "not-a-lock.txt", "inuse.30.lock"];
        var holders = SessionLocks.Inspect(files, pid => pid == 20); // only pid 20 alive

        Assert.Equal(3, holders.Count); // the non-lock file is ignored
        Assert.Single(SessionLocks.Live(holders));
        Assert.Equal(20, SessionLocks.Live(holders)[0].Pid);
        Assert.Equal(2, SessionLocks.Dead(holders).Count);
    }

    [Fact]
    public void Foreign_returns_live_holders_that_are_not_ours()
    {
        // pids 10, 20, 30 alive; 40 dead. 20 is our own ACP child.
        string[] files = ["inuse.10.lock", "inuse.20.lock", "inuse.30.lock", "inuse.40.lock"];
        var holders = SessionLocks.Inspect(files, pid => pid != 40);

        var foreign = SessionLocks.Foreign(holders, isOurs: pid => pid == 20);

        Assert.Equal(new[] { 10, 30 }, foreign.Select(h => h.Pid).OrderBy(p => p));
    }

    [Fact]
    public void Foreign_never_returns_our_own_pid_even_when_alive()
    {
        // Safety invariant: an agent that kills Foreign() must never take down
        // its own ACP child, so our pid is excluded regardless of liveness.
        string[] files = ["inuse.100.lock"];
        var holders = SessionLocks.Inspect(files, pid => true); // 100 is alive

        Assert.Empty(SessionLocks.Foreign(holders, isOurs: pid => pid == 100));
    }

    [Fact]
    public void Foreign_excludes_dead_holders()
    {
        string[] files = ["inuse.55.lock"];
        var holders = SessionLocks.Inspect(files, pid => false); // 55 is dead

        Assert.Empty(SessionLocks.Foreign(holders, isOurs: _ => false));
    }

    [Fact]
    public void Foreign_treats_all_live_holders_as_foreign_when_none_are_ours()
    {
        string[] files = ["inuse.7.lock", "inuse.8.lock"];
        var holders = SessionLocks.Inspect(files, pid => true);

        Assert.Equal(new[] { 7, 8 }, SessionLocks.Foreign(holders, isOurs: _ => false).Select(h => h.Pid).OrderBy(p => p));
    }

    [Fact]
    public void ReapDead_deletes_only_dead_owner_locks()
    {
        var live = Path.Combine(_dir, $"inuse.{Environment.ProcessId}.lock");
        var dead = Path.Combine(_dir, $"inuse.{DeadPid}.lock");
        File.WriteAllText(live, "");
        File.WriteAllText(dead, "");

        var reaped = SessionLocks.ReapDead(_dir);

        Assert.Equal(new[] { dead }, reaped);
        Assert.True(File.Exists(live), "the live holder's lock must be preserved");
        Assert.False(File.Exists(dead), "the dead holder's lock must be reaped");
    }

    [Fact]
    public void ReapDead_on_missing_directory_is_a_noop()
    {
        var reaped = SessionLocks.ReapDead(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(reaped);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
