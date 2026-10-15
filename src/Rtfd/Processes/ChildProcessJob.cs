using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rtfd.Processes;

/// <summary>
/// On Windows, puts rtfd into a job object that kills every process in it when the job's last
/// handle closes, which is when rtfd ends, however it ends. Every child rtfd starts after that
/// (git, the agent, the agent's own children such as <c>rtfd git-tools</c>) inherits the job, so
/// killing <c>serve</c> from Task Manager can't leave an agent running with the worktree open
/// (plan Phase 5's risk: a straggling child's handle keeps a folder from being deleted). On other
/// systems it does nothing; the startup sweep still runs.
/// </summary>
internal static partial class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    /// <summary>Never closed: the handle lives as long as the process, and closing it is the point.</summary>
    private static nint _job;

    /// <summary>Why the job couldn't be set up, or null when it is (or the system has none).</summary>
    public static string? Enable()
    {
        if (!OperatingSystem.IsWindows() || _job != 0)
        {
            return null;
        }

        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            return $"CreateJobObject failed ({Marshal.GetLastPInvokeError()})";
        }

        var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Unsafe.SizeOf<ExtendedLimitInformation>())
            || !AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            var error = Marshal.GetLastPInvokeError();
            CloseHandle(job);
            return $"rtfd couldn't join its job object ({error})";
        }

        _job = job;
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
