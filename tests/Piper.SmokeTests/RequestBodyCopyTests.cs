using System.Runtime.InteropServices;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;

// A request body is passed on without being copied: a cloned request shares the array (which is only
// ever replaced, never written into) and a large body is written from its own array rather than from
// one built behind its head. Discovered by reflection like every other group
// (public static Task Run*Async(TestRunner)); there is no registration line to look for.
internal static class RequestBodyCopyTests
{
    private const int SingleWriteBytes = 16 * 1024;

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a cloned request shares its body array and still cannot be changed through the clone", () =>
        {
            var original = new HttpRequestData { Method = "POST", RequestTarget = "/a", Body = Pattern(100) };
            original.Headers.Set("X-One", "1");
            var snapshot = original.Body.ToArray();

            var clone = original.Clone();
            runner.IsTrue(ReferenceEquals(original.Body, clone.Body), "the body array is shared, not copied");

            // What the proxy and the Composer do to a clone: change its head and replace its body.
            clone.Method = "PUT";
            clone.RequestTarget = "/b";
            clone.HttpVersion = "HTTP/1.1";
            clone.Headers.Set("X-One", "changed");
            clone.Headers.Set("Content-Length", "3");
            clone.Body = [1, 2, 3];

            runner.AreEqual("POST", original.Method, "the original's method is unchanged");
            runner.AreEqual("/a", original.RequestTarget, "its target is unchanged");
            runner.AreEqual("1", original.Headers["X-One"], "its headers are unchanged");
            runner.IsTrue(original.Headers["Content-Length"] is null, "no header added to it");
            runner.IsTrue(original.Body.AsSpan().SequenceEqual(snapshot), "its body is unchanged by the clone being given another");

            // The outbound request the proxy builds is such a clone: hop-by-hop headers go, framing is
            // rewritten, and none of it may show in the request the session keeps.
            original.Headers.Set("Connection", "keep-alive");
            original.Headers.Set("Transfer-Encoding", "chunked");
            var outbound = ProxyServer.BuildOutboundRequest(original, preserveUpgrade: false, new ProxyOptions());
            runner.IsTrue(outbound.Headers["Transfer-Encoding"] is null, "the outbound request lost Transfer-Encoding");
            runner.AreEqual("100", outbound.Headers["Content-Length"], "and was framed by length");
            runner.AreEqual("chunked", original.Headers["Transfer-Encoding"], "while the original still shows what the client sent");
            runner.IsTrue(original.Body.AsSpan().SequenceEqual(snapshot), "and its body bytes are untouched");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a request is written head first and body second, without copying a large body", async () =>
        {
            foreach (var size in new[] { 0, 1, SingleWriteBytes - 1, SingleWriteBytes, SingleWriteBytes + 1, 1024 * 1024 })
            {
                var request = new HttpRequestData { Method = "POST", RequestTarget = "/up?x=1", HttpVersion = "HTTP/1.1", Body = Pattern(size) };
                request.Headers.Set("Host", "example.test");
                request.Headers.Set("Content-Length", size.ToString());

                var recorder = new RecordingStream();
                await request.WriteOriginFormAsync(recorder, CancellationToken.None);

                var expectedHead = "POST /up?x=1 HTTP/1.1\r\nHost: example.test\r\nContent-Length: " + size + "\r\n\r\n";
                var all = recorder.Writes.SelectMany(w => w).ToArray();
                runner.AreEqual(expectedHead.Length + size, all.Length, $"{size} B: total bytes written");
                runner.AreEqual(expectedHead, Encoding.Latin1.GetString(all, 0, expectedHead.Length), $"{size} B: the head");
                runner.IsTrue(all.AsSpan(expectedHead.Length).SequenceEqual(request.Body), $"{size} B: the body");

                if (size is > 0 and <= SingleWriteBytes)
                    runner.AreEqual(1, recorder.Writes.Count, $"{size} B: a small body goes out in the one write it always did");
                else if (size > SingleWriteBytes)
                {
                    runner.AreEqual(2, recorder.Writes.Count, $"{size} B: a large body is a second write");
                    runner.IsTrue(ReferenceEquals(recorder.SecondWriteArray, request.Body), $"{size} B: written from the request's own array, not a copy");
                }
            }
        });
    }

    private static byte[] Pattern(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Keeps every write whole, so a test can see how many there were and whether one was the
    /// caller's own array.</summary>
    private sealed class RecordingStream : Stream
    {
        public List<byte[]> Writes { get; } = [];
        public byte[]? SecondWriteArray { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Writes.Count == 1 && MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Offset == 0
                && segment.Count == segment.Array!.Length)
                SecondWriteArray = segment.Array;
            Writes.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
