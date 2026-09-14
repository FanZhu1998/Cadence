using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cadence.Core.Diagnostics;

/// <summary>
/// A Windows Job Object configured to kill its members when the handle closes.
/// </summary>
/// <remarks>
/// Child processes launched for a usage probe must not outlive Cadence. Without this, a crash or a
/// forced quit while a probe is in flight leaves an orphaned CLI running indefinitely, and the
/// user finds a stray process holding their credentials open. Assigning the child to a
/// kill-on-close job makes the OS clean up regardless of how Cadence exits.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class JobObject : IDisposable
{
    private nint _handle;

    private JobObject(nint handle) => _handle = handle;

    /// <summary>Creates the job, or null when unsupported (non-Windows, or the call failed).</summary>
    public static JobObject? CreateKillOnClose()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var handle = CreateJobObjectW(nint.Zero, null);
        if (handle == nint.Zero) return null;

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new JobObject(handle);
    }

    /// <summary>Adds a process to the job. Returns false if it could not be assigned.</summary>
    public bool Assign(Process process)
    {
        if (_handle == nint.Zero) return false;

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            return false; // the process already exited
        }
    }

    public void Dispose()
    {
        if (_handle == nint.Zero) return;
        CloseHandle(_handle);
        _handle = nint.Zero;
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, nint info, uint infoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
