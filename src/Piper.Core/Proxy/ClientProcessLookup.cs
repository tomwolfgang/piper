using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Piper.Core.Proxy;

/// <summary>
/// Resolves the OS process that owns the client end of an accepted loopback TCP connection, so
/// captured sessions can show "chrome", "curl", etc. instead of just an IP:port. Windows-only, via
/// <c>iphlpapi.dll!GetExtendedTcpTable</c> -- there is no cross-platform equivalent, and Piper's
/// proxy only needs this for the local developer machine it runs on.
/// </summary>
/// <remarks>
/// <para>What is read: the system's table of established TCP connections (addresses, ports, owning
/// process id) for the one address family of the connection, and then, for the process id found, the
/// image file name of that process. Nothing else about the process is read, and neither the table nor
/// the name leaves this class except as the short name that ends up on the session.</para>
/// <para>Every input is hostile or racing: the table can change between the size probe and the read,
/// a process id can be reused, and the process can exit between the table read and the name read. None
/// of that throws out of <see cref="Resolve(IPEndPoint?, IPEndPoint?)"/> and a failed lookup is
/// <see cref="string.Empty"/>. The table read is at most <see cref="MaxTableAttempts"/> calls into a
/// buffer capped at <see cref="MaxTableBytes"/>, and the name cache is bounded.</para>
/// </remarks>
internal sealed class ClientProcessLookup
{
    internal const int MaxTableAttempts = 3;
    internal const int InitialTableBytes = 16 * 1024;
    internal const int MaxTableBytes = 8 * 1024 * 1024;
    internal const int MaxCachedProcesses = 256;
    internal static readonly TimeSpan DefaultNameTtl = TimeSpan.FromSeconds(2);

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint NO_ERROR = 0;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ImagePathChars = 1024;

    /// <summary>Reads the connection table of <paramref name="addressFamily"/> into
    /// <paramref name="buffer"/>; <paramref name="size"/> is the buffer's length in and the size
    /// needed or used out. Returns the Win32 error code. The native call, replaceable in tests.</summary>
    internal delegate uint TcpTableReader(int addressFamily, Span<byte> buffer, ref int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow4
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [InlineArray(4)]
    private struct Address6
    {
        private uint _element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow6
    {
        public Address6 LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public Address6 RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetExtendedTcpTable(
        ref byte pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, ref char lpExeName, ref int lpdwSize);

    /// <summary>The lookup the proxy uses.</summary>
    internal static ClientProcessLookup Shared { get; } = new();

    private readonly TcpTableReader _readTable;
    private readonly Func<uint, string?> _nameOf;
    private readonly Func<long> _clock;
    private readonly long _ttlTicks;
    private readonly int _maxEntries;
    private readonly object _gate = new();
    private readonly Dictionary<uint, (string Name, long Expires)> _names = [];
    private long _tableReads;
    private long _nameReads;

    internal ClientProcessLookup(
        TcpTableReader? readTable = null, Func<uint, string?>? nameOf = null, Func<long>? clock = null,
        TimeSpan? nameTtl = null, int maxEntries = MaxCachedProcesses)
    {
        _readTable = readTable ?? ReadTableNative;
        _nameOf = nameOf ?? NameOfProcess;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _ttlTicks = (long)((nameTtl ?? DefaultNameTtl).TotalSeconds * Stopwatch.Frequency);
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>How many times the connection table has been asked for.</summary>
    internal long TableReads => Interlocked.Read(ref _tableReads);

    /// <summary>How many times a process name had to be read (a cache miss).</summary>
    internal long NameReads => Interlocked.Read(ref _nameReads);

    /// <summary>How many process names are cached right now.</summary>
    internal int CachedNames
    {
        get { lock (_gate) return _names.Count; }
    }

    /// <summary>Returns the short process name (e.g. "chrome") owning the client end of the connection
    /// <paramref name="clientEndpoint"/> -&gt; <paramref name="proxyEndpoint"/> (the accepted socket's
    /// remote and local endpoints), or <see cref="string.Empty"/> on any failure: API unavailable, no
    /// such connection in the table, the process already exited, access denied. Both ends are matched
    /// so a stale row for a reused source port cannot name another process. Never throws.</summary>
    public static string Resolve(IPEndPoint? clientEndpoint, IPEndPoint? proxyEndpoint) =>
        Shared.Lookup(clientEndpoint, proxyEndpoint);

    internal string Lookup(IPEndPoint? clientEndpoint, IPEndPoint? proxyEndpoint)
    {
        if (clientEndpoint is null || proxyEndpoint is null) return string.Empty;

        try
        {
            if (!TryFindOwner(Normalize(clientEndpoint), Normalize(proxyEndpoint), out var pid)) return string.Empty;
            return NameFor(pid);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failing lookup must not take the connection with it; the session just shows no process.
            return string.Empty;
        }
    }

    /// <summary>An IPv4 client reaching a dual-stack listener appears as an IPv4-mapped IPv6 address
    /// but is an AF_INET row in the table.</summary>
    private static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    // ------------------------------------------------------------------ connection table

    private bool TryFindOwner(IPEndPoint client, IPEndPoint server, out uint pid)
    {
        pid = 0;
        if (client.AddressFamily != server.AddressFamily) return false;
        var family = client.AddressFamily switch
        {
            AddressFamily.InterNetwork => AF_INET,
            AddressFamily.InterNetworkV6 => AF_INET6,
            _ => 0,
        };
        if (family == 0) return false;

        byte[]? buffer = null;
        try
        {
            var capacity = InitialTableBytes;
            for (var attempt = 0; attempt < MaxTableAttempts; attempt++)
            {
                if (buffer is null || buffer.Length < capacity)
                {
                    if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(capacity);
                }

                var size = buffer.Length;
                Interlocked.Increment(ref _tableReads);
                var error = _readTable(family, buffer, ref size);
                if (error == NO_ERROR)
                    return family == AF_INET ? Find4(buffer, client, server, out pid) : Find6(buffer, client, server, out pid);

                // The table grew between the size probe and the read (or the buffer was just too small):
                // retry once with room to spare, but never past the cap.
                if (error != ERROR_INSUFFICIENT_BUFFER || size <= 0 || size > MaxTableBytes) return false;
                capacity = (int)Math.Min(MaxTableBytes, (long)size + (size >> 2) + 1024);
            }

            return false;
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Validates the table header against the buffer it claims to live in and returns its rows
    /// as <typeparamref name="TRow"/>; an empty or inconsistent table has no rows.</summary>
    private static ReadOnlySpan<TRow> Rows<TRow>(byte[] buffer) where TRow : struct
    {
        if (buffer.Length < sizeof(int)) return default;
        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var rowBytes = Unsafe.SizeOf<TRow>();
        if (count <= 0 || (long)count * rowBytes > buffer.Length - sizeof(int)) return default;
        return MemoryMarshal.Cast<byte, TRow>(buffer.AsSpan(sizeof(int), count * rowBytes));
    }

    private static bool Find4(byte[] buffer, IPEndPoint client, IPEndPoint server, out uint pid)
    {
        Span<byte> octets = stackalloc byte[4];
        client.Address.TryWriteBytes(octets, out _);
        var clientAddr = MemoryMarshal.Read<uint>(octets);
        server.Address.TryWriteBytes(octets, out _);
        var serverAddr = MemoryMarshal.Read<uint>(octets);

        uint any = 0;
        foreach (ref readonly var row in Rows<TcpRow4>(buffer))
        {
            if (row.OwningPid == 0 || row.LocalAddr != clientAddr || row.RemoteAddr != serverAddr
                || PortOf(row.LocalPort) != client.Port || PortOf(row.RemotePort) != server.Port)
                continue;

            if (row.State == MIB_TCP_STATE_ESTAB) { pid = row.OwningPid; return true; }
            if (any == 0) any = row.OwningPid; // a connection already on its way out still names its owner
        }

        pid = any;
        return any != 0;
    }

    private static bool Find6(byte[] buffer, IPEndPoint client, IPEndPoint server, out uint pid)
    {
        Span<byte> octets = stackalloc byte[16];
        client.Address.TryWriteBytes(octets, out _);
        var clientAddr = MemoryMarshal.Read<Address6>(octets);
        server.Address.TryWriteBytes(octets, out _);
        var serverAddr = MemoryMarshal.Read<Address6>(octets);

        uint any = 0;
        foreach (ref readonly var row in Rows<TcpRow6>(buffer))
        {
            if (row.OwningPid == 0 || PortOf(row.LocalPort) != client.Port || PortOf(row.RemotePort) != server.Port
                || !((ReadOnlySpan<uint>)row.LocalAddr).SequenceEqual(clientAddr)
                || !((ReadOnlySpan<uint>)row.RemoteAddr).SequenceEqual(serverAddr))
                continue;

            if (row.State == MIB_TCP_STATE_ESTAB) { pid = row.OwningPid; return true; }
            if (any == 0) any = row.OwningPid;
        }

        pid = any;
        return any != 0;
    }

    /// <summary>A port in a table row is network byte order in the low 16 bits of a DWORD, so it is
    /// byte-swapped to give the port number.</summary>
    private static int PortOf(uint raw) => (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    private static uint ReadTableNative(int addressFamily, Span<byte> buffer, ref int size) =>
        GetExtendedTcpTable(ref MemoryMarshal.GetReference(buffer), ref size, false, addressFamily, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);

    // ------------------------------------------------------------------ process name

    /// <summary>The process's name from a short-lived cache, read once per process id per TTL. A name
    /// that could not be read is cached too (as empty) so a protected process is not retried for every
    /// connection. The TTL is deliberately short: a process id can be reused, and a cached name must
    /// not outlive the process for long.</summary>
    private string NameFor(uint pid)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_names.TryGetValue(pid, out var cached) && now < cached.Expires) return cached.Name;
        }

        Interlocked.Increment(ref _nameReads);
        var name = _nameOf(pid) ?? string.Empty;

        lock (_gate)
        {
            if (!_names.ContainsKey(pid) && _names.Count >= _maxEntries)
            {
                foreach (var key in _names.Where(entry => now >= entry.Value.Expires).Select(entry => entry.Key).ToList())
                    _names.Remove(key);
                if (_names.Count >= _maxEntries) _names.Clear(); // all live: start over rather than grow
            }

            _names[pid] = (name, now + _ttlTicks);
        }

        return name;
    }

    /// <summary>Reads a process's image name without a snapshot of every process: open it for query
    /// only, ask for its image path, close the handle. A process that cannot be opened that way
    /// (protected, or the System process) falls back to <see cref="Process"/>, whose handle is always
    /// released. Null when neither can name it.</summary>
    private static string? NameOfProcess(uint pid)
    {
        using (var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid))
        {
            if (!handle.IsInvalid)
            {
                Span<char> path = stackalloc char[ImagePathChars];
                var length = path.Length;
                if (QueryFullProcessImageName(handle, 0, ref MemoryMarshal.GetReference(path), ref length) && length > 0 && length <= path.Length)
                    return ShortName(path[..length]);
            }
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null; // exited since the table was read, or not ours to look at
        }
    }

    /// <summary>The file name of an image path without a trailing ".exe" -- the form
    /// <see cref="Process.ProcessName"/> gives, so the Process column reads as it always has.</summary>
    internal static string ShortName(ReadOnlySpan<char> imagePath)
    {
        var separator = imagePath.LastIndexOfAny('\\', '/');
        var name = separator >= 0 ? imagePath[(separator + 1)..] : imagePath;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToString();
    }
}
