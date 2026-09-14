using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cadence.App.Tray;

/// <summary>
/// Keeps Cadence cheap to leave running.
/// </summary>
/// <remarks>
/// A quota monitor that shows up in Task Manager's "high impact" column has defeated its own
/// purpose: the first thing a user does about a resource hog is close it, and then it warns them
/// about nothing at all. This is worth a few lines of interop.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public static partial class ProcessFootprint
{
    /// <summary>
    /// Releases pages back to the OS after a burst of work.
    /// </summary>
    /// <remarks>
    /// Startup does a lot in a short window — JIT, XAML parsing, a full transcript scan — and the
    /// working set stays inflated long after none of it is needed. Passing -1 asks Windows to trim
    /// the working set; pages that turn out to be needed are faulted straight back in from RAM, so
    /// this trades a handful of soft faults for a much smaller resident footprint.
    /// <para>
    /// Only worth doing at genuine quiet points. Calling it on every refresh would trade the same
    /// pages back and forth forever.
    /// </para>
    /// </remarks>
    public static void Trim()
    {
        try
        {
            // Compact first so the trim has something to give back.
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            using var process = Process.GetCurrentProcess();
            SetProcessWorkingSetSizeEx(process.Handle, -1, -1, 0);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException
                                      or InvalidOperationException or PlatformNotSupportedException)
        {
            // Purely an optimisation; never worth surfacing.
        }
    }

    /// <summary>
    /// Puts the process into or out of Windows 11 Efficiency Mode.
    /// </summary>
    /// <remarks>
    /// Efficiency mode marks the process as background work: EcoQoS scheduling on hybrid CPUs, so
    /// Cadence runs on efficiency cores and stops competing with the user's actual work. Turned
    /// off while the flyout is open, because then it <em>is</em> the user's actual work.
    /// </remarks>
    public static void SetEfficiencyMode(bool enabled)
    {
        try
        {
            using var process = Process.GetCurrentProcess();

            var throttleState = new ProcessPowerThrottlingState
            {
                Version = ProcessPowerThrottlingCurrentVersion,
                ControlMask = ProcessPowerThrottlingExecutionSpeed,
                StateMask = enabled ? ProcessPowerThrottlingExecutionSpeed : 0,
            };

            var size = Marshal.SizeOf<ProcessPowerThrottlingState>();
            var buffer = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(throttleState, buffer, fDeleteOld: false);
                SetProcessInformation(process.Handle, ProcessPowerThrottling, buffer, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            // EcoQoS also expects the process to drop to idle priority; without this the scheduler
            // hint is largely ignored.
            process.PriorityClass = enabled ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException
                                      or InvalidOperationException or PlatformNotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Unsupported before Windows 11, and harmless to skip.
        }
    }

    private const int ProcessPowerThrottling = 4;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, int informationClass, nint information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(nint process, nint minimum, nint maximum, uint flags);
}
