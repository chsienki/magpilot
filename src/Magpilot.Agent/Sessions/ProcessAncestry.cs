using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Magpilot.Agent.Sessions;

/// <summary>
/// Walks the OS process tree to answer "does this copilot session lock belong
/// to a session a <c>magpilot</c> launcher is driving?" -- used by
/// <see cref="HostOwnershipReconciler"/> to reconstruct host ownership after
/// an agent restart wiped the in-memory map (the launcher registers ownership
/// once and never re-asserts, so a restart otherwise orphans the session into
/// "kill to unlock" even though the launcher is still driving it).
///
/// <para>A copilot child spawned by the launcher through a ConPTY is a direct,
/// walkable descendant of the launcher process (verified empirically); under
/// <c>--magpilot-agency</c> the chain is <c>magpilot -> agency -> copilot</c>.
/// So walking up from the lock's PID and finding a process named
/// <c>magpilot</c> identifies a launcher-driven session, distinct from the
/// agent's own <c>copilot --acp</c> children (parented under
/// <c>Magpilot.Agent</c>) and from bare terminal <c>copilot</c> sessions
/// (parented under a shell).</para>
///
/// Windows-only (agency + the installed launcher are Windows); returns false
/// elsewhere. A sibling <c>ProcessTree</c> in Magpilot.Host serves the
/// launcher's own agency-descendant check.
/// </summary>
internal static class ProcessAncestry
{
    /// <summary>
    /// Walk up from <paramref name="startPid"/> and return the PID of the
    /// nearest ancestor whose process name matches
    /// <paramref name="processName"/> (case-insensitive, extension stripped),
    /// or false if none. A visited-set guards against a cyclic snapshot.
    /// </summary>
    public static bool TryFindAncestorPidByName(int startPid, string processName, out int ancestorPid)
    {
        ancestorPid = 0;
        if (!OperatingSystem.IsWindows()) return false;

        var procs = Snapshot();
        var seen = new HashSet<int>();
        var cur = startPid;
        // Walk parents only (exclude startPid itself -- the lock holder is
        // copilot, never the launcher, so a self-match would be wrong).
        while (procs.TryGetValue(cur, out var node) && node.Parent != 0 && seen.Add(cur))
        {
            if (procs.TryGetValue(node.Parent, out var parentNode)
                && NameMatches(parentNode.Name, processName))
            {
                ancestorPid = node.Parent;
                return true;
            }
            cur = node.Parent;
        }
        return false;
    }

    private static bool NameMatches(string exeName, string wanted)
    {
        var baseName = Path.GetFileNameWithoutExtension(exeName);
        return string.Equals(baseName, wanted, StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Node(int Parent, string Name);

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, Node> Snapshot()
    {
        var map = new Dictionary<int, Node>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
            return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do { map[(int)entry.th32ProcessID] = new Node((int)entry.th32ParentProcessID, entry.szExeFile); }
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
