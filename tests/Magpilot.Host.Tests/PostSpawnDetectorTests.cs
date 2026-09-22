using Magpilot.Host;
using Xunit;

namespace Magpilot.Host.Tests;

public sealed class PostSpawnDetectorTests : IDisposable
{
    private const int CopilotPid = 424242;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"magpilot-session-switch-{Guid.NewGuid():N}");

    public PostSpawnDetectorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Watch_returns_session_that_gains_the_copilot_lock()
    {
        WriteSession("current", withLock: true);
        WriteSession("next", withLock: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watch = PostSpawnDetector.WaitForSessionSwitchAsync(
            CopilotPid,
            "current",
            timeout.Token,
            _root);

        await Task.Delay(500, timeout.Token);
        WriteLock("next");

        Assert.Equal("next", await watch);
    }

    [Fact]
    public async Task Watch_detects_switch_that_completed_before_watcher_started()
    {
        WriteSession("current", withLock: true);
        WriteSession("next", withLock: true);
        var baseline = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "current", "events.jsonl"),
            baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "current", "workspace.yaml"),
            baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "current", $"inuse.{CopilotPid}.lock"),
            baseline);

        var switched = await PostSpawnDetector.WaitForSessionSwitchAsync(
            CopilotPid,
            "current",
            CancellationToken.None,
            _root);

        Assert.Equal("next", switched);
    }

    [Fact]
    public async Task Watch_returns_existing_locked_session_when_activity_advances()
    {
        WriteSession("current", withLock: true);
        WriteSession("next", withLock: true);
        var eventsPath = Path.Combine(_root, "next", "events.jsonl");
        var baseline = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(eventsPath, baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "next", "workspace.yaml"),
            baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "next", $"inuse.{CopilotPid}.lock"),
            baseline);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watch = PostSpawnDetector.WaitForSessionSwitchAsync(
            CopilotPid,
            "current",
            timeout.Token,
            _root);

        await Task.Delay(500, timeout.Token);
        File.SetLastWriteTimeUtc(eventsPath, DateTime.UtcNow);

        Assert.Equal("next", await watch);
    }

    [Fact]
    public async Task Watch_uses_activity_fallback_when_new_lock_is_missing()
    {
        WriteSession("current", withLock: true);
        WriteSession("next", withLock: false);
        var eventsPath = Path.Combine(_root, "next", "events.jsonl");
        var baseline = DateTime.UtcNow.AddMinutes(-1);
        File.SetLastWriteTimeUtc(eventsPath, baseline);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "next", "workspace.yaml"),
            baseline);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watch = PostSpawnDetector.WaitForSessionSwitchAsync(
            CopilotPid,
            "current",
            timeout.Token,
            _root);

        await Task.Delay(500, timeout.Token);
        File.SetLastWriteTimeUtc(eventsPath, DateTime.UtcNow);

        Assert.Equal("next", await watch);
    }

    [Fact]
    public async Task Watch_ignores_activity_owned_by_another_live_process()
    {
        WriteSession("current", withLock: true);
        WriteSession("other", withLock: false);
        File.WriteAllText(
            Path.Combine(
                _root,
                "other",
                $"inuse.{Environment.ProcessId}.lock"),
            "");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = PostSpawnDetector.WaitForSessionSwitchAsync(
            CopilotPid,
            "current",
            timeout.Token,
            _root);

        await Task.Delay(500);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "other", "events.jsonl"),
            DateTime.UtcNow);

        Assert.Null(await watch);
    }

    [Fact]
    public void Remove_session_lock_deletes_only_the_selected_session()
    {
        WriteSession("current", withLock: true);
        WriteSession("other", withLock: true);

        Assert.True(PostSpawnDetector.RemoveSessionLock(
            "current",
            CopilotPid,
            _root));
        Assert.False(File.Exists(
            Path.Combine(_root, "current", $"inuse.{CopilotPid}.lock")));
        Assert.True(File.Exists(
            Path.Combine(_root, "other", $"inuse.{CopilotPid}.lock")));
    }

    [Fact]
    public void Find_most_recent_locked_session_tracks_a_quick_switch_back()
    {
        WriteSession("first", withLock: true);
        WriteSession("second", withLock: true);
        var baseline = DateTime.UtcNow.AddMinutes(-1);
        SetSessionTimes("first", baseline);

        Assert.Equal(
            "second",
            PostSpawnDetector.FindMostRecentLockedSession(
                CopilotPid,
                _root));

        SetSessionTimes("first", DateTime.UtcNow.AddSeconds(1));

        Assert.Equal(
            "first",
            PostSpawnDetector.FindMostRecentLockedSession(
                CopilotPid,
                _root));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private void WriteSession(string sessionId, bool withLock)
    {
        var directory = Directory.CreateDirectory(
            Path.Combine(_root, sessionId));
        File.WriteAllText(
            Path.Combine(directory.FullName, "workspace.yaml"),
            $"cwd: \"{_root}\"{Environment.NewLine}");
        File.WriteAllText(
            Path.Combine(directory.FullName, "events.jsonl"),
            "{}\n");
        if (withLock)
            WriteLock(sessionId);
    }

    private void WriteLock(string sessionId) =>
        File.WriteAllText(
            Path.Combine(
                _root,
                sessionId,
                $"inuse.{CopilotPid}.lock"),
            "");

    private void SetSessionTimes(string sessionId, DateTime timestamp)
    {
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, sessionId, "events.jsonl"),
            timestamp);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, sessionId, "workspace.yaml"),
            timestamp);
        File.SetLastWriteTimeUtc(
            Path.Combine(
                _root,
                sessionId,
                $"inuse.{CopilotPid}.lock"),
            timestamp);
    }
}
