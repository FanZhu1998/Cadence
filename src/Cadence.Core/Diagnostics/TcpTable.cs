using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cadence.Core.Diagnostics;

/// <summary>
/// Listening TCP sockets with their owning process ids, via IP Helper.
/// </summary>
/// <remarks>
/// The managed <c>IPGlobalProperties.GetActiveTcpListeners</c> does not report which process owns
/// a socket, which is exactly the part needed to tie a discovered language-server PID to the port
/// it is serving on.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class TcpTable
{
    public readonly record struct Listener(IPAddress Address, int Port, int ProcessId);

    /// <summary>Every loopback-capable listener owned by <paramref name="processId"/>.</summary>
    public static IReadOnlyList<Listener> ListenersForProcess(int processId)
        => [.. All().Where(l => l.ProcessId == processId)];

    /// <summary>All IPv4 TCP listeners with owning PIDs. Returns empty on any API failure.</summary>
    public static IReadOnlyList<Listener> All()
    {
        var size = 0;

        // First call sizes the buffer.
        var status = GetExtendedTcpTable(nint.Zero, ref size, order: false, AfInet, TcpTableOwnerPidListener, 0);
        if (status is not ErrorInsufficientBuffer and not NoError || size <= 0) return [];

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, order: false, AfInet, TcpTableOwnerPidListener, 0);
            if (status != NoError) return [];

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            var listeners = new List<Listener>(count);

            // The table is a DWORD count followed by `count` rows.
            var cursor = buffer + sizeof(int);
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(cursor);
                cursor += rowSize;

                listeners.Add(new Listener(
                    new IPAddress((long)row.LocalAddr),
                    // The port arrives as two bytes in network order inside a DWORD.
                    (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF)),
                    (int)row.OwningPid));
            }

            return listeners;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int NoError = 0;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial int GetExtendedTcpTable(
        nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, int reserved);
}
