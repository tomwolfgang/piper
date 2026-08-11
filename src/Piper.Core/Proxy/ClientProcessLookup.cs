using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Piper.Core.Proxy;

/// <summary>
/// Resolves the OS process that owns the local end of a loopback TCP connection, so captured
/// sessions can show "chrome", "curl", etc. instead of just an IP:port. Windows-only, via
/// <c>iphlpapi.dll!GetExtendedTcpTable</c> -- there is no cross-platform equivalent, and Piper's
/// proxy only needs this for the local developer machine it runs on.
/// </summary>
internal static class ClientProcessLookup
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int NO_ERROR = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    /// <summary>Returns the short process name (e.g. "chrome") owning the local TCP4 endpoint
    /// matching <paramref name="clientEndpoint"/>'s port, or <see cref="string.Empty"/> on any
    /// failure -- API unavailable, the process already exited, an unmatched or non-IPv4 endpoint,
    /// access denied, etc. Never throws.</summary>
    public static string Resolve(IPEndPoint? clientEndpoint)
    {
        if (clientEndpoint is null || clientEndpoint.AddressFamily != AddressFamily.InterNetwork)
            return string.Empty;

        var buffer = IntPtr.Zero;
        try
        {
            var bufferSize = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

            while (result == ERROR_INSUFFICIENT_BUFFER)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(bufferSize);
                result = GetExtendedTcpTable(buffer, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            }

            if (result != NO_ERROR || buffer == IntPtr.Zero)
                return string.Empty;

            var rowCount = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            var rowsStart = buffer + sizeof(int);

            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowsStart + i * rowSize);
                var localPort = SwapPort(row.LocalPort);
                if (localPort != clientEndpoint.Port) continue;

                try
                {
                    return Process.GetProcessById((int)row.OwningPid).ProcessName;
                }
                catch
                {
                    return string.Empty; // process already exited by the time we looked it up
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The port fields in MIB_TCPROW_OWNER_PID are stored in network byte order packed
    /// into the low-order bytes of the DWORD once marshaled as little-endian -- byte-swap to get
    /// the real port number.</summary>
    private static ushort SwapPort(uint raw) => (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}
