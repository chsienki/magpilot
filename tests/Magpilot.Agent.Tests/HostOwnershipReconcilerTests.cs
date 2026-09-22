using Magpilot.Agent.Sessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Magpilot.Agent.Tests;

public sealed class HostOwnershipReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"magpilot-host-reconcile-{Guid.NewGuid():N}");

    public HostOwnershipReconcilerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Runtime_resident_session_is_not_reclassified_as_host_owned()
    {
        const string sessionId = "runtime-owned";
        WriteLiveLock(sessionId);
        var ownership = CreateOwnership();
        var ancestryCalled = false;
        var reconciler = CreateReconciler(
            ownership,
            id => id == sessionId,
            _ =>
            {
                ancestryCalled = true;
                return (true, Environment.ProcessId);
            });

        reconciler.Reconcile();

        Assert.False(ancestryCalled);
        Assert.False(ownership.TryGet(sessionId, out _));
    }

    [Fact]
    public void Nonresident_launcher_descendant_is_reconciled()
    {
        const string sessionId = "host-owned";
        WriteLiveLock(sessionId);
        var ownership = CreateOwnership();
        var reconciler = CreateReconciler(
            ownership,
            _ => false,
            _ => (true, Environment.ProcessId));

        reconciler.Reconcile();

        Assert.True(ownership.TryGet(sessionId, out var entry));
        Assert.Equal(Environment.ProcessId, entry.HostPid);
    }

    [Fact]
    public void Reconcile_checks_every_live_lock_for_a_launcher_descendant()
    {
        const string sessionId = "multi-lock-host-owned";
        Directory.CreateDirectory(Path.Combine(_root, sessionId));
        var ownership = CreateOwnership();
        var reconciler = CreateReconciler(
            ownership,
            _ => false,
            pid => pid == 202
                ? (true, Environment.ProcessId)
                : (false, 0),
            _ => [101, 202]);

        reconciler.Reconcile();

        Assert.True(ownership.TryGet(sessionId, out var entry));
        Assert.Equal(Environment.ProcessId, entry.HostPid);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HostOwnershipReconciler CreateReconciler(
        HostOwnership ownership,
        Func<string, bool> isRuntimeResident,
        Func<int, (bool Found, int LauncherPid)> findLauncher,
        Func<string, IReadOnlyList<int>>? liveLockPids = null) =>
        new(
            new SessionScanner(
                NullLogger<SessionScanner>.Instance,
                _root),
            ownership,
            NullLogger<HostOwnershipReconciler>.Instance,
            new ConfigurationBuilder().Build(),
            isRuntimeResident,
            findLauncher,
            liveLockPids);

    private HostOwnership CreateOwnership() =>
        new(
            NullLogger<HostOwnership>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agent:HostOwnershipFile"] = Path.Combine(
                        _root,
                        "hostownership.json"),
                })
                .Build());

    private void WriteLiveLock(string sessionId)
    {
        var directory = Directory.CreateDirectory(
            Path.Combine(_root, sessionId));
        File.WriteAllText(
            Path.Combine(
                directory.FullName,
                $"inuse.{Environment.ProcessId}.lock"),
            "");
    }
}
