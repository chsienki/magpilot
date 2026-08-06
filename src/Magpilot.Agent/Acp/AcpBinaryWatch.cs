namespace Magpilot.Agent.Acp;

/// <summary>
/// Detects when the executable a long-lived ACP child launched from has been
/// replaced on disk since launch -- e.g. WinGet upgrading <c>copilot.exe</c> out
/// from under a running <c>--acp</c> child, which then keeps serving the old
/// image (renamed <c>copilot.exe.old</c>) for as long as it lives. Compares the
/// on-disk last-write time to the one captured at launch.
/// </summary>
public sealed class AcpBinaryWatch(string exePath, DateTime launchedWriteUtc)
{
    public string ExePath => exePath;

    /// <summary>Snapshot the binary's current write-time, or null if it can't be read.</summary>
    public static AcpBinaryWatch? Capture(string exePath)
    {
        try
        {
            var fi = new FileInfo(exePath);
            return fi.Exists ? new AcpBinaryWatch(exePath, fi.LastWriteTimeUtc) : null;
        }
        catch { return null; }
    }

    /// <summary>True if the on-disk binary is newer than the one we launched from.</summary>
    public bool IsStale()
    {
        try
        {
            var fi = new FileInfo(exePath);
            return fi.Exists && IsStale(launchedWriteUtc, fi.LastWriteTimeUtc);
        }
        catch { return false; }
    }

    /// <summary>Pure comparison: the binary was replaced if disk is newer than launch.</summary>
    public static bool IsStale(DateTime launchedWriteUtc, DateTime currentWriteUtc) =>
        currentWriteUtc > launchedWriteUtc;
}
