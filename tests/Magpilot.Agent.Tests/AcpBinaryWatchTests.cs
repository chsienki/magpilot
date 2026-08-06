using Magpilot.Agent.Acp;
using Xunit;

namespace Magpilot.Agent.Tests;

/// <summary>
/// Covers detection of an ACP child serving a replaced binary (the
/// copilot.exe.old drift). Pure comparison is table-tested; capture-then-replace
/// runs against a real temp file, matching the HostOwnershipTests convention.
/// </summary>
public sealed class AcpBinaryWatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"magpilot-bin-{Guid.NewGuid():N}");

    public AcpBinaryWatchTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(0L, 1L, true)]
    [InlineData(1L, 0L, false)]
    public void IsStale_true_only_when_disk_is_newer(long launchTicks, long currentTicks, bool expected) =>
        Assert.Equal(expected, AcpBinaryWatch.IsStale(
            new DateTime(launchTicks, DateTimeKind.Utc), new DateTime(currentTicks, DateTimeKind.Utc)));

    [Fact]
    public void Capture_then_replace_is_detected()
    {
        var exe = Path.Combine(_dir, "copilot.exe");
        File.WriteAllText(exe, "v1");
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(-10)); // unambiguously older launch time

        var watch = AcpBinaryWatch.Capture(exe);
        Assert.NotNull(watch);
        Assert.False(watch!.IsStale());

        // Simulate WinGet rewriting copilot.exe in place.
        File.WriteAllText(exe, "v2");
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow);
        Assert.True(watch.IsStale());
    }

    [Fact]
    public void Capture_returns_null_for_a_missing_binary() =>
        Assert.Null(AcpBinaryWatch.Capture(Path.Combine(_dir, "nope.exe")));

    [Fact]
    public void IsStale_is_false_if_the_binary_vanishes()
    {
        var exe = Path.Combine(_dir, "gone.exe");
        File.WriteAllText(exe, "x");
        var watch = AcpBinaryWatch.Capture(exe)!;

        File.Delete(exe);
        Assert.False(watch.IsStale()); // missing file is not "newer" -- no false alarm
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
