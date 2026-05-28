using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Magpilot.Agent.Acp;

/// <summary>
/// Windows Job Object that ties spawned <c>copilot --acp</c> children to
/// the lifetime of this agent process. When the agent dies for any
/// reason (clean shutdown, taskkill, crash, OOM, user signing out), the
/// last handle to the job is closed and Windows kernel atomically kills
/// every process in the job -- including grandchildren the copilot
/// child has spawned (bash subprocesses, MCP servers, etc).
///
/// Without this, an agent restart leaves the prior copilot child alive
/// holding all its session locks (<c>inuse.&lt;pid&gt;.lock</c> files).
/// The new agent's SessionScanner then classifies those sessions as
/// "Locked by another PID" and the user has to either Stop-Process the
/// orphan or click "kill PID and adopt" in the SPA to recover.
///
/// On non-Windows this class is a no-op. POSIX does NOT auto-kill
/// orphans -- the OS reparents them to init (PID 1) and they keep
/// running. Magnus avoids the orphan bug on Linux today only because
/// it runs in a Docker container: the container's PID namespace gets
/// reaped by the kernel when tini (PID 1) exits, killing the agent's
/// orphan copilot. For a bare-metal Linux deployment this fix would
/// need a Linux equivalent (likely prctl PR_SET_PDEATHSIG via a tiny
/// preload shim, since the call has to come from the child) --
/// tracked as `agent-linux-orphan-protection` in copilot-instructions.
///
/// Singleton-by-static: the job handle is created lazily on first
/// <see cref="Attach"/> call and held in a static field for the
/// process lifetime. There's no Dispose path -- the OS closes the
/// handle when the process exits, which is exactly the trigger we
/// want for the kill-on-close behaviour.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32JobObject
{
    private static readonly object _lock = new();
    private static IntPtr _jobHandle = IntPtr.Zero;

    /// <summary>
    /// Add <paramref name="process"/> to the agent's kill-on-close job.
    /// Safe to call multiple times; the job is created on the first
    /// call and reused thereafter. No-op on non-Windows and if the
    /// process has already exited.
    /// </summary>
    public static void Attach(Process process, ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (process.HasExited) return;

        var job = EnsureJob(log);
        if (job == IntPtr.Zero) return;

        if (!AssignProcessToJobObject(job, process.Handle))
        {
            // ERROR_ACCESS_DENIED (5) on Windows < 8 if the process
            // was already in another job. Modern Windows allows
            // nested jobs so this should always succeed on 10/11/2022+.
            // Logged at Warning so a regression is visible in
            // /admin/logs without breaking startup.
            var err = Marshal.GetLastWin32Error();
            log?.LogWarning(
                "AssignProcessToJobObject(pid={Pid}) failed: Win32 error {Err}",
                process.Id, err);
        }
    }

    private static IntPtr EnsureJob(ILogger? log)
    {
        if (_jobHandle != IntPtr.Zero) return _jobHandle;
        lock (_lock)
        {
            if (_jobHandle != IntPtr.Zero) return _jobHandle;

            var job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
            if (job == IntPtr.Zero)
            {
                log?.LogWarning(
                    "CreateJobObject failed: Win32 error {Err}",
                    Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var infoPtr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, infoPtr, fDeleteOld: false);
                if (!SetInformationJobObject(
                        job,
                        JobObjectExtendedLimitInformation,
                        infoPtr,
                        (uint)size))
                {
                    log?.LogWarning(
                        "SetInformationJobObject failed: Win32 error {Err}",
                        Marshal.GetLastWin32Error());
                    // Job exists but lacks the kill-on-close flag -- not
                    // useful, fall back to no-op. The job handle leaks
                    // until process exit, which is fine (the OS reaps it).
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            _jobHandle = job;
            log?.LogInformation(
                "Win32 Job Object created (kill-on-close). " +
                "All spawned copilot --acp children will die with the agent.");
            return job;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
