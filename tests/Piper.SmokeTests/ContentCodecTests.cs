using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Text;

/// <summary>
/// Content-Encoding is applied to attacker-supplied bytes: a few KiB of gzip can promise gigabytes. These
/// tests pin the bound, the truncation behaviour and the per-body cache the inspector relies on.
/// </summary>
internal static class ContentCodecTests
{
    /// <summary>What a decode may reach. Stated as a number here so a silent change of the limit is noticed.</summary>
    private const int Cap = 64 * 1024 * 1024;

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a gzip bomb decodes to a body cut at the cap, not to its full size", () =>
        {
            runner.AreEqual(Cap, ContentCodec.MaxDecodedBytes, "the documented cap");

            var bomb = Compress(new byte[Cap + 1024 * 1024], "gzip");
            runner.IsTrue(bomb.Length < 1024 * 1024, "the bomb really is small while compressed");

            var decoded = ContentCodec.Decode(bomb, "gzip");
            runner.AreEqual(Cap, decoded.Length, "the decoded body stops exactly at the cap");

            var bounded = ContentCodec.DecodeBounded(bomb, "gzip");
            runner.IsTrue(bounded.Truncated, "and says it was cut");
            return Task.CompletedTask;
        });

        await runner.RunAsync("stacked encodings share one cap instead of multiplying", () =>
        {
            // Each layer of this stack is itself highly compressible, so decoding it right to left
            // expands at every step; the last step alone would be far past the cap.
            var stacked = new byte[Cap + 1024 * 1024];
            for (var layer = 0; layer < 3; layer++) stacked = Compress(stacked, "gzip");

            var bounded = ContentCodec.DecodeBounded(stacked, "gzip, gzip, gzip");
            runner.IsTrue(bounded.Bytes.Length <= Cap, $"three stacked gzip layers stay within the cap (got {bounded.Bytes.Length})");
            runner.IsTrue(bounded.Truncated, "and the result says it was cut");

            // A mixed stack (br over gzip) under a small cap: the cap is a parameter of the whole decode.
            var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("stacked ", 50_000)));
            var twice = Compress(Compress(payload, "gzip"), "br");
            var small = ContentCodec.DecodeBounded(twice, "gzip, br", 1024);
            runner.IsTrue(small.Bytes.Length <= 1024, "a small cap binds a stacked decode");
            runner.IsTrue(small.Truncated, "a stacked decode over a small cap says it was cut");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a Content-Encoding stacking too many layers is left undecoded", () =>
        {
            var text = Encoding.ASCII.GetBytes("nested");
            var layered = text;
            var header = new List<string>();
            for (var layer = 0; layer < ContentCodec.MaxStackedEncodings; layer++)
            {
                layered = Compress(layered, "gzip");
                header.Add("gzip");
            }

            runner.AreEqual("nested", Encoding.ASCII.GetString(ContentCodec.Decode(layered, string.Join(", ", header))),
                "a stack at the limit decodes");

            var tooMany = Compress(layered, "gzip");
            header.Add("gzip");
            var refused = ContentCodec.DecodeBounded(tooMany, string.Join(", ", header));
            runner.IsTrue(ReferenceEquals(tooMany, refused.Bytes) && !refused.Truncated, "one layer over the limit leaves the body alone");

            var thousands = ContentCodec.DecodeBounded(tooMany, string.Join(", ", Enumerable.Repeat("gzip", 5000)));
            runner.IsTrue(ReferenceEquals(tooMany, thousands.Bytes), "thousands of layers are refused without decoding any");
            return Task.CompletedTask;
        });

        await runner.RunAsync("deflate, raw deflate and brotli bombs are cut at the cap too", () =>
        {
            var zeros = new byte[Cap + 1024 * 1024];
            foreach (var (encoding, header) in new[] { ("zlib", "deflate"), ("deflate", "deflate"), ("br", "br") })
            {
                var decoded = ContentCodec.DecodeBounded(Compress(zeros, encoding), header);
                runner.AreEqual(Cap, decoded.Bytes.Length, $"a {encoding} bomb stops at the cap");
                runner.IsTrue(decoded.Truncated, $"a {encoding} bomb is flagged");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("output exactly at the cap is whole; one byte more is truncated", () =>
        {
            const int limit = 4096;
            foreach (var (encoding, header) in new[] { ("gzip", "gzip"), ("zlib", "deflate"), ("deflate", "deflate"), ("br", "br") })
            {
                var atCap = ContentCodec.DecodeBounded(Compress(new byte[limit], encoding), header, limit);
                runner.AreEqual(limit, atCap.Bytes.Length, $"{encoding}: the whole body fits");
                runner.IsTrue(!atCap.Truncated, $"{encoding}: a body of exactly the cap is not truncated");

                var over = ContentCodec.DecodeBounded(Compress(new byte[limit + 1], encoding), header, limit);
                runner.AreEqual(limit, over.Bytes.Length, $"{encoding}: one byte over is cut to the cap");
                runner.IsTrue(over.Truncated, $"{encoding}: one byte over is truncated");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("a compressed body cut short decodes as far as it goes", () =>
        {
            var text = string.Concat(Enumerable.Repeat("partial capture ", 20_000));
            foreach (var (encoding, header) in new[] { ("gzip", "gzip"), ("zlib", "deflate"), ("deflate", "deflate"), ("br", "br") })
            {
                var whole = Compress(Encoding.ASCII.GetBytes(text), encoding);
                var decoded = ContentCodec.Decode(whole[..(whole.Length / 2)], header);
                var shown = Encoding.ASCII.GetString(decoded);
                runner.IsTrue(shown.Length > 0 && text.StartsWith(shown, StringComparison.Ordinal),
                    $"{encoding}: the readable start of a cut stream is shown, not the compressed bytes");
            }

            // A stream that dies part-way through is flagged, and keeps what it produced first.
            var corrupt = Compress(Encoding.ASCII.GetBytes(text), "gzip");
            corrupt[corrupt.Length / 2] ^= 0xFF;
            var bounded = ContentCodec.DecodeBounded(corrupt, "gzip");
            runner.IsTrue(bounded.Truncated, "a stream corrupt part-way is flagged truncated");
            runner.IsTrue(bounded.Bytes.Length > 0 && bounded.Bytes.Length < text.Length,
                "and keeps the bytes decoded before the fault");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a body that is not what its Content-Encoding says is left alone", () =>
        {
            var plain = Encoding.UTF8.GetBytes("this was never compressed");
            foreach (var header in new[] { "gzip", "deflate", "br", "gzip, br", "br, zstd", "zstd", "  ", ",", "made-up" })
            {
                var decoded = ContentCodec.DecodeBounded(plain, header);
                runner.IsTrue(ReferenceEquals(plain, decoded.Bytes), $"'{header}': the original bytes come back");
                runner.IsTrue(!decoded.Truncated, $"'{header}': and nothing is flagged");
            }

            runner.AreEqual(0, ContentCodec.DecodeBounded([], "gzip").Bytes.Length, "an empty body stays empty");
            return Task.CompletedTask;
        });

        await runner.RunAsync("hostile compressed bytes never throw out of a decode", () =>
        {
            var random = new Random(20260930);
            var valid = Compress(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("fuzz me ", 400))), "gzip");
            var failures = new List<string>();

            foreach (var header in new[] { "gzip", "x-gzip", "deflate", "br", "gzip, br", "deflate, gzip, br" })
            {
                for (var i = 0; i < 300; i++)
                {
                    var input = i % 3 == 0 ? RandomBytes(random, random.Next(0, 600)) : Mutate(random, valid);
                    try
                    {
                        var decoded = ContentCodec.DecodeBounded(input, header, 64 * 1024);
                        if (decoded.Bytes.Length > 64 * 1024) failures.Add($"{header}: over the cap");
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{header}: {ex.GetType().Name}");
                    }
                }
            }

            runner.AreEqual(string.Empty, string.Join("; ", failures.Distinct()), "no input escapes as an exception or past the cap");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the shared bounded read cuts, flags and never hides an out-of-memory", () =>
        {
            var plain = Encoding.ASCII.GetBytes(new string('x', 5000));
            var whole = ContentCodec.BoundedDecompress(new MemoryStream(plain), 5000);
            runner.IsTrue(!whole.Truncated && whole.Bytes.Length == 5000, "a stream that fits is returned whole");

            var cut = ContentCodec.BoundedDecompress(new MemoryStream(plain), 4999);
            runner.IsTrue(cut.Truncated && cut.Bytes.Length == 4999, "one byte over the cap is cut and flagged");

            var faulted = ContentCodec.BoundedDecompress(new FaultingStream(plain, new InvalidDataException("corrupt")), 10_000);
            runner.IsTrue(faulted.Truncated && faulted.Bytes.Length == 5000, "an InvalidDataException keeps what was read and flags it");

            var ioFault = ContentCodec.BoundedDecompress(new FaultingStream(plain, new IOException("broken")), 10_000);
            runner.IsTrue(ioFault.Truncated && ioFault.Bytes.Length == 5000, "an IOException is handled the same way");

            try
            {
                ContentCodec.BoundedDecompress(new FaultingStream(plain, new OutOfMemoryException()), 10_000);
                runner.IsTrue(false, "an OutOfMemoryException escapes");
            }
            catch (OutOfMemoryException)
            {
                runner.IsTrue(true, "an OutOfMemoryException escapes");
            }

            try
            {
                ContentCodec.BoundedDecompress(new MemoryStream(plain), 0);
                runner.IsTrue(false, "a zero cap is refused");
            }
            catch (ArgumentOutOfRangeException)
            {
                runner.IsTrue(true, "a zero cap is refused");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("a message decodes its body once however often it is read", () =>
        {
            var response = new HttpResponseData { Body = Compress(Encoding.UTF8.GetBytes("cached body"), "gzip") };
            response.Headers.Set("Content-Encoding", "gzip");

            var first = response.DecodedBody;
            runner.AreEqual("cached body", Encoding.UTF8.GetString(first), "the body decodes");
            runner.IsTrue(ReferenceEquals(first, response.DecodedBody), "a second read reuses the decoded array");
            runner.IsTrue(!response.Decoded.Truncated, "a whole body is not flagged");

            response.Body = Compress(Encoding.UTF8.GetBytes("replaced"), "gzip");
            runner.AreEqual("replaced", Encoding.UTF8.GetString(response.DecodedBody), "a replaced body is decoded afresh");

            response.Headers.Set("Content-Encoding", "identity");
            runner.IsTrue(ReferenceEquals(response.Body, response.DecodedBody), "a changed encoding is not answered from the cache");

            response.Headers.Set("Content-Encoding", "gzip");
            runner.AreEqual("replaced", Encoding.UTF8.GetString(response.DecodedBody), "and changing it back decodes again");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a body that failed to decode is remembered as such, not retried", () =>
        {
            var response = new HttpResponseData { Body = Encoding.UTF8.GetBytes("plain, whatever the header claims") };
            response.Headers.Set("Content-Encoding", "gzip");
            runner.IsTrue(ReferenceEquals(response.Body, response.DecodedBody), "an undecodable body reads as itself");
            runner.IsTrue(ReferenceEquals(response.Body, response.DecodedBody), "every time");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the decode cache stays within its budget and lets released bodies go", () =>
        {
            var before = ContentCodec.CachedDecodeBytes;
            var alive = new List<HttpResponseData>();
            for (var i = 0; i < 100; i++)
            {
                // Distinct 1 MiB bodies, kept alive together: 100 MiB decoded, more than the budget.
                var payload = new byte[1024 * 1024];
                BitConverter.TryWriteBytes(payload, i + 1);
                var response = new HttpResponseData { Body = Compress(payload, "gzip") };
                response.Headers.Set("Content-Encoding", "gzip");
                _ = response.DecodedBody;
                alive.Add(response);
            }

            runner.IsTrue(ContentCodec.CachedDecodeBytes - before <= 64L * 1024 * 1024,
                $"the cache holds at most 64 MiB while every body is alive (holding {ContentCodec.CachedDecodeBytes - before})");
            runner.AreEqual(1024 * 1024, alive[^1].DecodedBody.Length, "a body past the budget still decodes correctly");

            var held = ContentCodec.CachedDecodeBytes;
            alive.Clear();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            runner.IsTrue(ContentCodec.CachedDecodeBytes < held, "bodies that were released give their share of the budget back");

            // Threads decoding at once must not each see room and together pass the ceiling.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var baseline = ContentCodec.CachedDecodeBytes;
            var concurrent = new List<HttpResponseData>();
            for (var i = 0; i < 64; i++)
            {
                var payload = new byte[4 * 1024 * 1024];
                BitConverter.TryWriteBytes(payload, i + 1000);
                var response = new HttpResponseData { Body = Compress(payload, "gzip") };
                response.Headers.Set("Content-Encoding", "gzip");
                concurrent.Add(response);
            }

            Parallel.ForEach(concurrent, new ParallelOptions { MaxDegreeOfParallelism = 16 }, response => _ = response.DecodedBody);
            runner.IsTrue(ContentCodec.CachedDecodeBytes - baseline <= 64L * 1024 * 1024,
                $"concurrent decodes stay within the budget (holding {ContentCodec.CachedDecodeBytes - baseline})");
            GC.KeepAlive(concurrent);

            // Threads that race on the same body decode it more than once but cache it once.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var sameBaseline = ContentCodec.CachedDecodeBytes;
            var shared = new HttpResponseData { Body = Compress(new byte[1024 * 1024], "gzip") };
            shared.Headers.Set("Content-Encoding", "gzip");
            Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ => shared.DecodedBody.GetHashCode());
            runner.IsTrue(ContentCodec.CachedDecodeBytes - sameBaseline <= 1024 * 1024,
                $"racing on one body holds one copy of budget, not one per thread (holding {ContentCodec.CachedDecodeBytes - sameBaseline})");
            GC.KeepAlive(shared);
            return Task.CompletedTask;
        });

        await runner.RunAsync("the decode cache does not keep a released body alive", () =>
        {
            var (weak, decodedWeak) = DecodeAndDrop();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            runner.IsTrue(!weak.TryGetTarget(out _), "the body array is collectable once the message is gone");
            runner.IsTrue(!decodedWeak.TryGetTarget(out _), "and so is its decoded copy");
            return Task.CompletedTask;
        });

        await runner.RunAsync("search, URLWithBody and text views inherit the bound through the text path", () =>
        {
            var bomb = Compress(new byte[Cap + 1024 * 1024], "gzip");
            var text = HttpMessage.TextOf(bomb, "text/plain", "gzip");
            runner.AreEqual(Cap, text.Length, "text of a bomb is cut at the cap");

            var response = new HttpResponseData { Body = bomb };
            response.Headers.Set("Content-Encoding", "gzip");
            response.Headers.Set("Content-Type", "text/plain");
            runner.AreEqual(Cap, response.BodyAsText().Length, "BodyAsText on a message is cut at the cap");
            runner.IsTrue(response.Decoded.Truncated, "the message reports its decoded body as truncated");
            runner.AreEqual(Cap, response.DecodedBody.Length, "and DecodedBody is bounded too");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard inflates up to its cap and no further", () =>
        {
            const int inflateCap = 1024 * 1024;
            var atCap = TextTransforms.Apply(TextTransform.ToDeflatedSaml, new string('a', inflateCap));
            runner.AreEqual(inflateCap, TextTransforms.Apply(TextTransform.FromDeflatedSaml, atCap).Length,
                "input that inflates to exactly the cap is accepted");

            var overCap = TextTransforms.Apply(TextTransform.ToDeflatedSaml, new string('a', inflateCap + 1));
            try
            {
                TextTransforms.Apply(TextTransform.FromDeflatedSaml, overCap);
                runner.IsTrue(false, "one byte past the cap is rejected");
            }
            catch (InvalidDataException)
            {
                runner.IsTrue(true, "one byte past the cap is rejected");
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>Decodes a message's body and lets the message go, reporting what should now be collectable.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<byte[]> Body, WeakReference<byte[]> Decoded) DecodeAndDrop()
    {
        var body = Compress(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("released ", 1000))), "gzip");
        var response = new HttpResponseData { Body = body };
        response.Headers.Set("Content-Encoding", "gzip");
        return (new WeakReference<byte[]>(body), new WeakReference<byte[]>(response.DecodedBody));
    }

    /// <summary>Compresses <paramref name="data"/> the way a server would label it: gzip, zlib, raw deflate or br.</summary>
    private static byte[] Compress(byte[] data, string encoding)
    {
        using var target = new MemoryStream();
        using (Stream compressor = encoding switch
        {
            "gzip" => new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true),
            "zlib" => new ZLibStream(target, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new DeflateStream(target, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(target, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        })
        {
            compressor.Write(data);
        }
        return target.ToArray();
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A valid stream with a byte flipped, or cut short, or both.</summary>
    private static byte[] Mutate(Random random, byte[] valid)
    {
        var copy = (byte[])valid.Clone();
        copy[random.Next(copy.Length)] ^= (byte)random.Next(1, 256);
        return random.Next(3) == 0 ? copy[..random.Next(1, copy.Length)] : copy;
    }

    /// <summary>Yields <paramref name="data"/> and then throws, the way a decompressor meets a bad block.</summary>
    private sealed class FaultingStream(byte[] data, Exception fault) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length) throw fault;
            var take = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
