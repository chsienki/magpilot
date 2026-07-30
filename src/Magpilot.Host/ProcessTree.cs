using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Magpilot.Host;

/// <summary>
/// Minimal process-tree lookups. Used by <see cref="PostSpawnDetector"/> to
/// recognize a copilot session lock when copilot is a grandchild rather than
/// the process we spawned directly -- e.g. under <c>--magpilot-agency</c>,
/// where the tree is <c>magpilot -> agency -> copilot</c> and copilot's
/// <c>inuse.&lt;pid&gt;.lock</c> carries a PID the launcher never sees.
/// </summary>
internal static class ProcessTree
{
    /// <summary>
    /// Snapshot of every running process's parent PID (child PID -> parent
    /// PID). Empty on non-Windows -- the only caller is the agency detection
    /// path, which is Windows-only. A snapshot is a point-in-time view; the
    /// caller rebuilds it each poll tick so newly-spawned children appear.
    /// </summary>
    public static IReadOnlyDictionary<int, int> SnapshotParentMap()
    {
        return OperatingSystem.IsWindows() ? SnapshotParentMapWindows() : new Dictionary<int, int>();
    }

    /// <summary>
    /// True if <paramref name="pid"/> is <paramref name="ancestorPid"/> or a
    /// descendant of it, per the supplied parent map. Pure (no OS calls) so it
    /// is unit-testable; walks parent links with a visited-set guard so a
    /// corrupt map with a cycle can't spin forever.
    /// </summary>
    public static bool IsSelfOrDescendant(int pid, int ancestorPid, IReadOnlyDictionary<int, int> parents)
    {
        if (pid == ancestorPid) return true;
        var seen = new HashSet<int>();
        var cur = pid;
        while (seen.Add(cur) && parents.TryGetValue(cur, out var parent) && parent != 0)
        {
            if (parent == ancestorPid) return true;
            cur = parent;
        }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, int> SnapshotParentMapWindows()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
            return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do { map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
