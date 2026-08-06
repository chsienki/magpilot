using Magpilot.Agent.Sessions;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Covers the freshness watermark that drives stale-resume reload: a session is
/// stale only when its events.jsonl has grown past what our child last served.
/// Pure comparison is table-tested; the size tracking runs against real temp
/// files, matching the HostOwnershipTests convention.
/// </summary>
public sealed class SessionFreshnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"magpilot-fresh-{Guid.NewGuid():N}");

    public SessionFreshnessTests() => Directory.CreateDirectory(_dir);

    private string Events(string sid)
    {
        var d = Path.Combine(_dir, sid);
        Directory.CreateDirectory(d);
        return Path.Combine(d, "events.jsonl");
    }

    [Theory]
    [InlineData(100, 100, false)]
    [InlineData(100, 101, true)]
    [InlineData(100, 50, false)]
    public void IsStale_is_true_only_when_disk_grew(long served, long current, bool expected) =>
        Assert.Equal(expected, SessionFreshness.IsStale(served, current));

    [Fact]
    public void Unseen_session_is_never_stale()
    {
        var freshness = new SessionFreshness();
        var path = Events("s1");
        File.WriteAllText(path, "line\n");

        Assert.False(freshness.MayBeStale("s1", path)); // never RecordServed
    }

    [Fact]
    public void Session_becomes_stale_when_the_file_grows_after_serving()
    {
        var freshness = new SessionFreshness();
        var path = Events("s2");
        File.WriteAllText(path, "one\n");
        freshness.RecordServed("s2", path);
        Assert.False(freshness.MayBeStale("s2", path));

        File.AppendAllText(path, "two\n"); // a foreign writer advances disk
        Assert.True(freshness.MayBeStale("s2", path));

        freshness.RecordServed("s2", path); // we reload + resync
        Assert.False(freshness.MayBeStale("s2", path));
    }

    [Fact]
    public void Forget_clears_the_watermark()
    {
        var freshness = new SessionFreshness();
        var path = Events("s3");
        File.WriteAllText(path, "x\n");
        freshness.RecordServed("s3", path);
        File.AppendAllText(path, "y\n");
        Assert.True(freshness.MayBeStale("s3", path));

        freshness.Forget("s3");
        Assert.False(freshness.MayBeStale("s3", path)); // unseen again
    }

    [Fact]
    public void Watermark_is_zero_for_a_missing_file() =>
        Assert.Equal(0, SessionFreshness.Watermark(Path.Combine(_dir, "nope", "events.jsonl")));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
