using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Piper.Core.Proxy;

// Naming the process on the other end of an accepted connection: against the real connection table
// over IPv4 and IPv6, against hand-built tables (matching, several rows, malformed and hostile
// ones), against a table read that fails or keeps growing, and for the cache and the handle the old
// lookup leaked.
internal static class ClientProcessLookupTests
{
    private static readonly IPEndPoint Server4 = new(IPAddress.Loopback, 8888);
    private static readonly IPEndPoint Server6 = new(IPAddress.IPv6Loopback, 8888);

    public static async Task RunClientProcessLookupAsync(TestRunner runner)
    {
        var self = OwnProcessName();

        await runner.RunAsync("the process on a real IPv4 loopback connection is named", async () =>
        {
            using var pair = await Pair.OpenAsync(IPAddress.Loopback);
            runner.AreEqual(self, new ClientProcessLookup().Lookup(pair.ClientEnd, pair.ServerEnd), "this process is found");
            runner.AreEqual(self, ClientProcessLookup.Resolve(pair.ClientEnd, pair.ServerEnd), "and so by the shared lookup the proxy uses");
        });

        await runner.RunAsync("the process on a real IPv6 loopback connection is named", async () =>
        {
            if (!Socket.OSSupportsIPv6 || !CanBind(IPAddress.IPv6Loopback))
            {
                runner.ToolMissing("this machine has no IPv6 loopback (::1); the IPv6 lookup was not exercised");
                return;
            }

            using var pair = await Pair.OpenAsync(IPAddress.IPv6Loopback);
            runner.AreEqual(self, new ClientProcessLookup().Lookup(pair.ClientEnd, pair.ServerEnd), "this process is found over ::1");
        });

        await runner.RunAsync("an unrelated port, or a wrong far end, is unknown", async () =>
        {
            using var pair = await Pair.OpenAsync(IPAddress.Loopback);
            var lookup = new ClientProcessLookup();
            var unused = new IPEndPoint(IPAddress.Loopback, FreePort());
            runner.AreEqual("", lookup.Lookup(unused, pair.ServerEnd), "a source port nobody holds names nothing");
            runner.AreEqual("", lookup.Lookup(pair.ClientEnd, new IPEndPoint(IPAddress.Loopback, pair.ServerEnd.Port + 1)),
                "a real source port with another far end is not a match (a reused port cannot name a stranger)");
            runner.AreEqual("", lookup.Lookup(null, pair.ServerEnd), "no client endpoint");
            runner.AreEqual("", lookup.Lookup(pair.ClientEnd, null), "no local endpoint");
            runner.AreEqual("", lookup.Lookup(new IPEndPoint(IPAddress.Loopback, pair.ClientEnd.Port), new IPEndPoint(IPAddress.IPv6Loopback, pair.ServerEnd.Port)),
                "mixed address families");
        });

        await runner.RunAsync("only a row matching both ends names a process; established beats closing; pid 0 is skipped", () =>
        {
            var client = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 50000);
            var other = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 50001);
            var table = Table4(
                (Server4, client, 11, 5),   // the proxy's own side of the same connection: not the client
                (client, other, 12, 5),     // right source port, wrong far end
                (other, Server4, 13, 5),    // another client entirely
                (client, Server4, 0, 11),   // a TIME_WAIT row with no owner
                (client, Server4, 14, 8),   // CLOSE_WAIT: on its way out
                (client, Server4, 15, 5));  // ESTABLISHED
            var lookup = Fake(2, table, pid => $"p{pid}");
            runner.AreEqual("p15", lookup.Lookup(client, Server4), "the established row wins over a closing one");
            runner.AreEqual("p13", lookup.Lookup(other, Server4), "another client resolves to its own row");

            var closing = Fake(2, Table4((client, Server4, 14, 8)), pid => $"p{pid}");
            runner.AreEqual("p14", closing.Lookup(client, Server4), "a closing connection still names its owner when it is the only row");
            runner.AreEqual("", Fake(2, Table4((client, Server4, 0, 11)), pid => $"p{pid}").Lookup(client, Server4), "a row with no owner is unknown");
            return Task.CompletedTask;
        });

        await runner.RunAsync("IPv6 rows are matched on both addresses and ports, and an IPv4-mapped client is an IPv4 row", () =>
        {
            var client = new IPEndPoint(IPAddress.IPv6Loopback, 50010);
            var neighbour = new IPEndPoint(IPAddress.Parse("fe80::1"), 50010);
            var table = Table6((client, Server6, 21, 5), (neighbour, Server6, 22, 5), (Server6, client, 23, 5));
            var lookup = Fake(23, table, pid => $"p{pid}");
            runner.AreEqual("p21", lookup.Lookup(client, Server6), "the ::1 client row");
            runner.AreEqual("p22", lookup.Lookup(neighbour, Server6), "a different address with the same port is a different row");
            runner.AreEqual("", lookup.Lookup(new IPEndPoint(IPAddress.IPv6Loopback, 50011), Server6), "an unmatched port is unknown");

            var v4 = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 50020);
            var mapped = new ClientProcessLookup((int family, Span<byte> buffer, ref int size) =>
            {
                Table4((v4, Server4, 31, 5)).CopyTo(buffer);
                return family == 2 ? 0u : 87u;
            }, pid => $"p{pid}");
            runner.AreEqual("p31", mapped.Lookup(new IPEndPoint(v4.Address.MapToIPv6(), v4.Port), new IPEndPoint(Server4.Address.MapToIPv6(), Server4.Port)),
                "a dual-stack listener's mapped endpoints find the AF_INET row");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a name is cached per process id, expires after the TTL and the cache stays bounded", () =>
        {
            var client = new IPEndPoint(IPAddress.Loopback, 50030);
            var table = Table4((client, Server4, 41, 5));
            var now = 0L;
            var nameReads = 0;
            var lookup = new ClientProcessLookup(
                (int family, Span<byte> buffer, ref int size) => { table.CopyTo(buffer); return 0; },
                pid => { nameReads++; return "cached"; },
                () => now, TimeSpan.FromSeconds(1), maxEntries: 8);

            runner.AreEqual("cached", lookup.Lookup(client, Server4), "first call");
            runner.AreEqual("cached", lookup.Lookup(client, Server4), "second call, same name");
            runner.AreEqual(1, nameReads, "the process was read once");
            runner.AreEqual(2L, lookup.TableReads, "the connection table is still read for every connection (the row, not the pid, identifies it)");

            now += Stopwatch.Frequency / 2;
            lookup.Lookup(client, Server4);
            runner.AreEqual(1, nameReads, "inside the TTL it is still the cached name");

            now += Stopwatch.Frequency;
            lookup.Lookup(client, Server4);
            runner.AreEqual(2, nameReads, "past the TTL the process is read again");

            // Many distinct clients, one process id each.
            var rows = Enumerable.Range(0, 200).Select(i => (new IPEndPoint(IPAddress.Loopback, 51000 + i), Server4, (uint)(1000 + i), 5u)).ToArray();
            var big = Table4(rows);
            var bounded = new ClientProcessLookup(
                (int family, Span<byte> buffer, ref int size) => { big.CopyTo(buffer); return 0; },
                pid => "n" + pid, () => now, TimeSpan.FromHours(1), maxEntries: 8);
            var named = 0;
            var peak = 0;
            for (var i = 0; i < 200; i++)
            {
                if (bounded.Lookup(new IPEndPoint(IPAddress.Loopback, 51000 + i), Server4) == "n" + (1000 + i)) named++;
                peak = Math.Max(peak, bounded.CachedNames);
            }

            runner.AreEqual(200, named, "every one of 200 distinct processes is named");
            runner.IsTrue(peak <= 8, $"the cache never held more than 8 names (peak {peak})");
            runner.IsTrue(bounded.CachedNames is > 0 and <= 8, "and it is not simply empty");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a name that cannot be read is cached as unknown, and a failing name read is unknown", () =>
        {
            var client = new IPEndPoint(IPAddress.Loopback, 50040);
            var table = Table4((client, Server4, 51, 5));
            var reads = 0;
            var denied = new ClientProcessLookup(
                (int family, Span<byte> buffer, ref int size) => { table.CopyTo(buffer); return 0; },
                pid => { reads++; return null; }, nameTtl: TimeSpan.FromHours(1));
            runner.AreEqual("", denied.Lookup(client, Server4), "an unreadable name is unknown");
            denied.Lookup(client, Server4);
            runner.AreEqual(1, reads, "and is not retried within the TTL");

            var throwing = new ClientProcessLookup(
                (int family, Span<byte> buffer, ref int size) => { table.CopyTo(buffer); return 0; },
                pid => throw new InvalidOperationException("the process went away"));
            runner.AreEqual("", throwing.Lookup(client, Server4), "a name read that throws does not reach the caller");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a malformed, empty or failing table is unknown and never throws", () =>
        {
            var client = new IPEndPoint(IPAddress.Loopback, 50050);
            ClientProcessLookup With(ClientProcessLookup.TcpTableReader reader) => new(reader, pid => "x");

            runner.AreEqual("", With((int f, Span<byte> b, ref int s) => { b.Clear(); return 0; }).Lookup(client, Server4), "an all-zero table has no rows");
            runner.AreEqual("", With((int f, Span<byte> b, ref int s) => 5).Lookup(client, Server4), "access denied");
            runner.AreEqual("", With((int f, Span<byte> b, ref int s) => throw new DllNotFoundException()).Lookup(client, Server4), "the API is missing");
            runner.AreEqual("", With((int f, Span<byte> b, ref int s) => throw new InvalidOperationException()).Lookup(client, Server4), "the read throws");

            foreach (var count in new[] { -1, int.MinValue, int.MaxValue, 1_000_000, 700 })
            {
                var hostile = With((int f, Span<byte> b, ref int s) =>
                {
                    b.Clear();
                    BinaryPrimitives.WriteInt32LittleEndian(b, count);
                    return 0;
                });
                runner.AreEqual("", hostile.Lookup(client, Server4), $"a header claiming {count} rows in a 16 KB buffer has no usable rows");
            }

            // A row count that fits but rows that are garbage: nothing matches, nothing throws.
            var garbage = With((int f, Span<byte> b, ref int s) =>
            {
                b.Fill(0xFF);
                BinaryPrimitives.WriteInt32LittleEndian(b, 100);
                return 0;
            });
            runner.AreEqual("", garbage.Lookup(client, Server4), "garbage rows match nothing");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the table read is retried a bounded number of times into a bounded buffer", () =>
        {
            var client = new IPEndPoint(IPAddress.Loopback, 50060);
            var table = Table4((client, Server4, 61, 5));

            // The table keeps growing: every read says the buffer is too small.
            var growing = new ClientProcessLookup((int f, Span<byte> b, ref int s) => { s += 4096; return 122; }, pid => "x");
            runner.AreEqual("", growing.Lookup(client, Server4), "a table that never settles is unknown");
            runner.AreEqual((long)ClientProcessLookup.MaxTableAttempts, growing.TableReads, "after exactly the allowed number of attempts");

            // A table too big for the cap is not allocated.
            var huge = new ClientProcessLookup((int f, Span<byte> b, ref int s) => { s = ClientProcessLookup.MaxTableBytes + 1; return 122; }, pid => "x");
            runner.AreEqual("", huge.Lookup(client, Server4), "a table past the cap is unknown");
            runner.AreEqual(1L, huge.TableReads, "without a retry");

            var nonsense = new ClientProcessLookup((int f, Span<byte> b, ref int s) => { s = -5; return 122; }, pid => "x");
            runner.AreEqual("", nonsense.Lookup(client, Server4), "a nonsense size is unknown");
            runner.AreEqual(1L, nonsense.TableReads, "without a retry");

            // The buffer outgrows its first size when the table did, and the retry then succeeds.
            var firstLength = -1;
            var lengths = new List<int>();
            var regrown = new ClientProcessLookup((int f, Span<byte> b, ref int s) =>
            {
                lengths.Add(b.Length);
                if (firstLength < 0) { firstLength = b.Length; s = b.Length * 3; return 122; }
                table.CopyTo(b);
                return 0;
            }, pid => "x" + pid);
            runner.AreEqual("x61", regrown.Lookup(client, Server4), "a table that grew between the probe and the read is found on the retry");
            runner.IsTrue(lengths.Count == 2 && lengths[1] >= firstLength * 3, $"into a larger buffer ({string.Join(" -> ", lengths)} bytes)");
            return Task.CompletedTask;
        });

        await runner.RunAsync("repeated real lookups do not leak process handles", async () =>
        {
            using var pair = await Pair.OpenAsync(IPAddress.Loopback);
            var lookup = new ClientProcessLookup(nameTtl: TimeSpan.Zero); // every lookup reads the process again
            lookup.Lookup(pair.ClientEnd, pair.ServerEnd); // warm up lazily loaded pieces before counting

            var before = HandleCount();
            for (var i = 0; i < 1500; i++) lookup.Lookup(pair.ClientEnd, pair.ServerEnd);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var growth = HandleCount() - before;

            runner.AreEqual(1501L, lookup.NameReads, "every one of them read the process");
            runner.IsTrue(growth < 100, $"1500 lookups grew the handle count by {growth} (a leak would be 1500 or more)");
        });
    }

    // ------------------------------------------------------------------ helpers

    private static string OwnProcessName()
    {
        using var process = Process.GetCurrentProcess();
        return process.ProcessName;
    }

    private static long HandleCount()
    {
        using var process = Process.GetCurrentProcess();
        return process.HandleCount;
    }

    private static bool CanBind(IPAddress address)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A lookup whose table read returns <paramref name="table"/> for <paramref name="family"/>
    /// (2 or 23) and an empty table for the other.</summary>
    private static ClientProcessLookup Fake(int family, byte[] table, Func<uint, string?> nameOf) =>
        new((int requested, Span<byte> buffer, ref int size) =>
        {
            if (requested == family) table.CopyTo(buffer);
            else buffer.Clear();
            return 0;
        }, nameOf);

    /// <summary>A GetExtendedTcpTable TCP4 result: the row count, then 24-byte rows.</summary>
    private static byte[] Table4(params (IPEndPoint Local, IPEndPoint Remote, uint Pid, uint State)[] rows)
    {
        var bytes = new byte[4 + rows.Length * 24];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = bytes.AsSpan(4 + i * 24, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(row, rows[i].State);
            rows[i].Local.Address.TryWriteBytes(row[4..], out _);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], NetworkPort(rows[i].Local.Port));
            rows[i].Remote.Address.TryWriteBytes(row[12..], out _);
            BinaryPrimitives.WriteUInt32LittleEndian(row[16..], NetworkPort(rows[i].Remote.Port));
            BinaryPrimitives.WriteUInt32LittleEndian(row[20..], rows[i].Pid);
        }

        return bytes;
    }

    /// <summary>The TCP6 equivalent: 56-byte rows.</summary>
    private static byte[] Table6(params (IPEndPoint Local, IPEndPoint Remote, uint Pid, uint State)[] rows)
    {
        var bytes = new byte[4 + rows.Length * 56];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = bytes.AsSpan(4 + i * 56, 56);
            rows[i].Local.Address.TryWriteBytes(row, out _);
            BinaryPrimitives.WriteUInt32LittleEndian(row[20..], NetworkPort(rows[i].Local.Port));
            rows[i].Remote.Address.TryWriteBytes(row[24..], out _);
            BinaryPrimitives.WriteUInt32LittleEndian(row[44..], NetworkPort(rows[i].Remote.Port));
            BinaryPrimitives.WriteUInt32LittleEndian(row[48..], rows[i].State);
            BinaryPrimitives.WriteUInt32LittleEndian(row[52..], rows[i].Pid);
        }

        return bytes;
    }

    private static uint NetworkPort(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    /// <summary>A real established loopback connection; the server side's endpoints are what the proxy sees.</summary>
    private sealed class Pair : IDisposable
    {
        private readonly TcpClient _client;
        private readonly TcpClient _accepted;

        private Pair(TcpClient client, TcpClient accepted)
        {
            _client = client;
            _accepted = accepted;
            ClientEnd = (IPEndPoint)accepted.Client.RemoteEndPoint!;
            ServerEnd = (IPEndPoint)accepted.Client.LocalEndPoint!;
        }

        public IPEndPoint ClientEnd { get; }
        public IPEndPoint ServerEnd { get; }

        public static async Task<Pair> OpenAsync(IPAddress address)
        {
            var listener = new TcpListener(address, 0);
            listener.Start();
            try
            {
                var client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, ((IPEndPoint)listener.LocalEndpoint).Port);
                return new Pair(client, await listener.AcceptTcpClientAsync());
            }
            finally
            {
                listener.Stop();
            }
        }

        public void Dispose()
        {
            _accepted.Dispose();
            _client.Dispose();
        }
    }
}
