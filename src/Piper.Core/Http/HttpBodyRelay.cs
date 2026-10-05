using System.Buffers;
using System.Globalization;
using System.Text;

namespace Piper.Core.Http;

/// <summary>
/// Copies a message body from one connection to another as it arrives, keeping a bounded copy of
/// the start of it for the capture.
/// </summary>
/// <remarks>
/// <para>
/// Reading a body in full before forwarding any of it is the difference between a proxy a large
/// download can be run through and one it cannot. The client sees nothing at all until the last
/// byte has arrived, so a download of any size looks indistinguishable from a hang, and a stream
/// that never ends -- server-sent events, a long poll, live media -- can never be relayed at all.
/// </para>
/// <para>
/// Framing is preserved rather than recomputed. A length-delimited body keeps the origin's
/// Content-Length and is copied verbatim; a chunked body stays chunked. That is what every
/// comparable proxy does when it streams, and it is load-bearing: a client that reports progress
/// from Content-Length shows a frozen bar for the whole transfer if the length is dropped, which
/// looks exactly like the hang this exists to fix.
/// </para>
/// </remarks>
public static class HttpBodyRelay
{
    private const int TransferBufferBytes = 64 * 1024;

    /// <param name="Captured">The retained prefix of the body, which is the whole body unless the
    /// capture limit was reached.</param>
    /// <param name="TotalBytes">How many body bytes actually crossed the wire, whether kept or not.</param>
    /// <param name="Complete">False when <paramref name="Captured"/> holds only part of the body.</param>
    public readonly record struct Result(byte[] Captured, long TotalBytes, bool Complete);

    /// <summary>
    /// Relays a body of the given framing from <paramref name="source"/> to
    /// <paramref name="destination"/>, returning at most <paramref name="captureLimit"/> bytes of it.
    /// </summary>
    /// <param name="rechunkDownstream">
    /// True when the head already written downstream announced chunked transfer coding, so each
    /// relayed run of bytes must be framed as a chunk and the terminator written at the end.
    /// </param>
    /// <param name="onProgress">
    /// Given the running total of body bytes after each run is forwarded, on the relaying thread.
    /// Called for every read, so it must be no more than a store: whoever displays the figure reads
    /// it at their own pace rather than being told about every chunk.
    /// </param>
    public static async Task<Result> RelayAsync(
        HttpStreamReader source, HttpBodyDescriptor framing, Stream destination,
        bool rechunkDownstream, long captureLimit, Action<long>? onProgress, CancellationToken ct)
    {
        if (framing.Framing == HttpBodyFraming.None)
            return new Result([], 0, Complete: true);

        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferBytes);
        using var captured = new MemoryStream();
        long total = 0;

        try
        {
            switch (framing.Framing)
            {
                case HttpBodyFraming.Length:
                    total = await RelayLengthAsync(
                        source, framing.Length, destination, rechunkDownstream, buffer, captured, captureLimit, onProgress, ct)
                        .ConfigureAwait(false);
                    break;

                case HttpBodyFraming.Chunked:
                    total = await RelayChunkedAsync(
                        source, destination, rechunkDownstream, buffer, captured, captureLimit, onProgress, ct)
                        .ConfigureAwait(false);
                    break;

                default:
                    total = await RelayUntilCloseAsync(
                        source, destination, rechunkDownstream, buffer, captured, captureLimit, onProgress, ct)
                        .ConfigureAwait(false);
                    break;
            }

            if (rechunkDownstream)
                await destination.WriteAsync(Terminator, ct).ConfigureAwait(false);

            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new Result(captured.ToArray(), total, captured.Length == total);
    }

    private static readonly byte[] Terminator = "0\r\n\r\n"u8.ToArray();

    private static async Task<long> RelayLengthAsync(
        HttpStreamReader source, long length, Stream destination, bool rechunk,
        byte[] buffer, MemoryStream captured, long captureLimit, Action<long>? onProgress, CancellationToken ct)
    {
        long total = 0;
        while (total < length)
        {
            var want = (int)Math.Min(buffer.Length, length - total);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                // The head promising `length` bytes has already gone downstream and cannot be
                // retracted, so the caller has to abandon the connection rather than reuse it.
                throw new HttpParseException(
                    $"Origin closed after {total} of {length} promised body bytes.");
            }

            await ForwardAsync(destination, buffer, read, rechunk, ct).ConfigureAwait(false);
            Capture(captured, buffer, read, captureLimit);
            total += read;
            onProgress?.Invoke(total);
        }
        return total;
    }

    private static async Task<long> RelayUntilCloseAsync(
        HttpStreamReader source, Stream destination, bool rechunk,
        byte[] buffer, MemoryStream captured, long captureLimit, Action<long>? onProgress, CancellationToken ct)
    {
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return total;

            await ForwardAsync(destination, buffer, read, rechunk, ct).ConfigureAwait(false);
            Capture(captured, buffer, read, captureLimit);
            total += read;
            onProgress?.Invoke(total);
        }
    }

    /// <summary>
    /// De-chunks the origin's body and re-frames it downstream. Deliberately not a verbatim copy of
    /// the chunked bytes: parsing them is what rejects malformed framing rather than passing it on,
    /// and it is the only way to know how large the body actually was. Chunk boundaries are an
    /// artefact of how the origin happened to flush, not information, so they are not preserved.
    /// </summary>
    private static async Task<long> RelayChunkedAsync(
        HttpStreamReader source, Stream destination, bool rechunk,
        byte[] buffer, MemoryStream captured, long captureLimit, Action<long>? onProgress, CancellationToken ct)
    {
        long total = 0;
        while (true)
        {
            var sizeLine = await source.ReadLineAsync(ct).ConfigureAwait(false);
            if (sizeLine is null) throw new HttpParseException("Connection closed inside a chunked body.");

            if (!HttpSyntax.TryParseChunkSize(sizeLine, out var chunkSize))
                throw new HttpParseException($"Bad chunk size: '{HttpParser.Truncate(sizeLine)}'");

            if (chunkSize == 0)
            {
                // Consume trailers up to the terminating blank line. They are dropped rather than
                // forwarded, which is what buffering did before and what the announced downstream
                // framing (a plain chunked body, no Trailer header) describes.
                await HttpParser.SkipTrailersAsync(source, ct).ConfigureAwait(false);
                return total;
            }

            var remaining = chunkSize;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new HttpParseException($"Connection closed {remaining} bytes into a chunk.");

                await ForwardAsync(destination, buffer, read, rechunk, ct).ConfigureAwait(false);
                Capture(captured, buffer, read, captureLimit);
                total += read;
                remaining -= read;
                onProgress?.Invoke(total);
            }

            // Each chunk is followed by its own CRLF, and by nothing else: a chunk that runs on
            // past its declared size means the two ends disagree about where it stops.
            if (await source.ReadLineAsync(ct).ConfigureAwait(false) is not "")
                throw new HttpParseException("Chunk not terminated by CRLF.");
        }
    }

    private static async Task ForwardAsync(
        Stream destination, byte[] buffer, int count, bool rechunk, CancellationToken ct)
    {
        if (rechunk)
        {
            var header = Encoding.ASCII.GetBytes(count.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
            await destination.WriteAsync(header, ct).ConfigureAwait(false);
        }

        await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);

        if (rechunk)
            await destination.WriteAsync(Crlf, ct).ConfigureAwait(false);

        // Flushed per run rather than at the end: the point of relaying is that the client can act
        // on bytes now, and a buffered stream that holds them back reintroduces the wait.
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    /// <summary>
    /// Keeps the start of the body, up to the limit.
    /// </summary>
    /// <remarks>
    /// Bounded here, as the bytes go by, and not when something later reads the capture. A copy
    /// taken now and trimmed later has already cost the memory it was supposed to save -- a trap
    /// worth naming, because HTTP Toolkit shipped exactly that bug (mockttp#209): its body limit
    /// only applied once a reader attached, so an unread copy of every download was retained in
    /// full regardless.
    /// </remarks>
    private static void Capture(MemoryStream captured, byte[] buffer, int count, long captureLimit)
    {
        var room = captureLimit - captured.Length;
        if (room <= 0) return;
        captured.Write(buffer, 0, (int)Math.Min(count, room));
    }
}
