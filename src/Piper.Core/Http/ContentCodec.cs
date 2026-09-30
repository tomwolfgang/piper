using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace Piper.Core.Http;

/// <summary>A body with its Content-Encoding removed, and whether all of it is there.</summary>
/// <param name="Bytes">The decoded bytes, or the original bytes when nothing could be decoded.</param>
/// <param name="Truncated">
/// True when the decoded view is known to be incomplete or unreliable because some layer was cut:
/// its output hit the size cap, or its compressed stream was corrupt part-way. When the last layer
/// was cut, <paramref name="Bytes"/> holds what was decoded up to that point. When an earlier layer
/// was cut and a later one then could not read the cut stream, <paramref name="Bytes"/> is the
/// original (still encoded) body and this is still true. False means no layer was cut; it does not
/// mean the body was decoded (an unknown encoding, or a body that is not really compressed, comes
/// back as the original bytes with False). A stream that simply ends early (a captured prefix of a
/// large download) cannot be told apart from a complete one by the decompressors, so callers that
/// care also check <see cref="HttpMessage.IsBodyComplete"/>.
/// </param>
public readonly record struct DecodedContent(byte[] Bytes, bool Truncated);

/// <summary>Content-Encoding and charset handling for display purposes.</summary>
public static class ContentCodec
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Most bytes a decode will produce, however small the compressed input. A few KiB of gzip can
    /// promise gigabytes, so the ceiling is what keeps a hostile body from exhausting memory. It is twice
    /// what the proxy will retain of a body (<c>ProxyOptions.MaxCapturedBodyBytes</c>).
    /// </summary>
    public const int MaxDecodedBytes = 64 * 1024 * 1024;

    /// <summary>Most encodings a Content-Encoding header may stack before the body is left undecoded.</summary>
    public const int MaxStackedEncodings = 8;

    /// <summary>
    /// Strips Content-Encoding so the body can be shown as text, keeping at most
    /// <see cref="MaxDecodedBytes"/> of the result. Returns the input unchanged if it cannot be
    /// decoded at all - a debugger must never lose the original bytes to a decode error.
    /// </summary>
    public static byte[] Decode(byte[] body, string? contentEncoding) => DecodeBounded(body, contentEncoding).Bytes;

    /// <summary>
    /// <see cref="Decode"/>, with the size cap as a parameter and the result saying whether it was
    /// cut short. Encodings are applied right-to-left per RFC 9110, and every layer is held to the
    /// same <paramref name="maxBytes"/>, so a stack such as <c>gzip, gzip, gzip</c> cannot multiply
    /// the expansion: neither an intermediate nor the final body is ever larger than the cap.
    /// </summary>
    public static DecodedContent DecodeBounded(byte[] body, string? contentEncoding, int maxBytes = MaxDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (body.Length == 0 || string.IsNullOrWhiteSpace(contentEncoding)) return new DecodedContent(body, false);

        var encodings = contentEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Every layer is bounded in size, but a header naming thousands of them would still make
        // thousands of decompressor runs. No real server stacks more than a couple.
        if (encodings.Length > MaxStackedEncodings) return new DecodedContent(body, false);

        var current = body;
        var truncated = false;
        for (var i = encodings.Length - 1; i >= 0; i--)
        {
            // A layer that fails leaves the original bytes, but if an earlier layer was already cut
            // (so this one was handed a cut stream) the result must still say the view is incomplete.
            if (!TryDecodeOne(current, encodings[i], maxBytes, out var decoded, out var cut))
                return new DecodedContent(body, truncated);
            current = decoded;
            truncated |= cut;
        }
        return new DecodedContent(current, truncated);
    }

    public static bool IsKnownEncoding(string encoding) =>
        encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase)
        || encoding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase)
        || encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase)
        || encoding.Equals("br", StringComparison.OrdinalIgnoreCase)
        || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)
        || encoding.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static bool TryDecodeOne(byte[] input, string encoding, int maxBytes, out byte[] output, out bool truncated)
    {
        output = input;
        truncated = false;
        if (encoding.Length == 0
            || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)
            || encoding.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase)
            || encoding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase))
            return Run(input, s => new GZipStream(s, CompressionMode.Decompress), maxBytes, out output, out truncated);
        if (encoding.Equals("br", StringComparison.OrdinalIgnoreCase))
            return Run(input, s => new BrotliStream(s, CompressionMode.Decompress), maxBytes, out output, out truncated);
        if (encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase))
            return TryDecodeDeflate(input, maxBytes, out output, out truncated);
        return false; // unknown (e.g. zstd) - leave the body alone
    }

    /// <summary>
    /// Servers disagree on whether "deflate" means zlib (RFC 1950) or raw (RFC 1951). Zlib is tried
    /// first. Raw is tried only when zlib produced nothing and failed, which is what a stream without a
    /// zlib header does. A stream that yields any zlib output, or fills the cap, is a zlib stream: there
    /// is nothing raw could add, and trying it would hold a second cap's worth of memory (and let crafted
    /// bytes that also parse as a short raw stream win over zlib's correct prefix).
    /// </summary>
    private static bool TryDecodeDeflate(byte[] input, int maxBytes, out byte[] output, out bool truncated) =>
        Run(input, s => new ZLibStream(s, CompressionMode.Decompress), maxBytes, out output, out truncated)
        || Run(input, s => new DeflateStream(s, CompressionMode.Decompress), maxBytes, out output, out truncated);

    /// <summary>
    /// Decodes <paramref name="input"/> with the decompressor <paramref name="wrap"/> builds. False
    /// means the stream failed before producing anything, so it was not this encoding (or not
    /// compressed at all) and the caller keeps the original bytes.
    /// </summary>
    private static bool Run(byte[] input, Func<Stream, Stream> wrap, int maxBytes, out byte[] output, out bool truncated)
    {
        using var source = new MemoryStream(input, writable: false);
        using var decompressor = wrap(source);
        // Compressed payloads can be large. Avoid reserving four times their size up front (and
        // immediately landing on the LOH); the buffer grows as the decompressor produces data.
        (output, truncated) = BoundedDecompress(decompressor, maxBytes, (int)Math.Min((long)input.Length * 2, 1024 * 1024));
        return output.Length > 0 || !truncated;
    }

    /// <summary>
    /// Reads a decompressing stream to its end, keeping at most <paramref name="maxBytes"/>. The cap is
    /// enforced as the data arrives, so a decompression bomb is never materialised and noticed
    /// afterwards, and the one place that does this is shared by everything that inflates untrusted
    /// bytes (HTTP bodies and the TextWizard's SAML decoder).
    /// </summary>
    /// <returns>
    /// The bytes read, and true when they are not the whole stream: the output reached the cap with more
    /// still to come, or the stream turned out to be corrupt (<see cref="InvalidDataException"/>,
    /// <see cref="IOException"/> or, from brotli, <see cref="InvalidOperationException"/>), in which case
    /// the bytes decoded before the fault are returned. A stream that fails before its first byte
    /// therefore gives an empty array and true. Anything else, <see cref="OutOfMemoryException"/>
    /// included, propagates.
    /// </returns>
    internal static (byte[] Bytes, bool Truncated) BoundedDecompress(Stream decompressor, int maxBytes, int capacityHint = 0)
    {
        ArgumentNullException.ThrowIfNull(decompressor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        // Read straight into the result and grow it by doubling, but never past the cap: a stream
        // filling a 64 MiB cap must not reserve 128 MiB on the way.
        var buffer = new byte[Math.Min(Math.Max(capacityHint, 4096), maxBytes)];
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    // Full at the cap: the stream is whole only if it has nothing more to give.
                    if (length == maxBytes) return (buffer, decompressor.ReadByte() >= 0);
                    Array.Resize(ref buffer, (int)Math.Min(maxBytes, (long)length * 2));
                }

                var read = decompressor.Read(buffer, length, buffer.Length - length);
                if (read == 0) return (Trim(buffer, length), false);
                length += read;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
        {
            // What the decompressors throw for hostile bytes: gzip and deflate raise
            // InvalidDataException, brotli raises InvalidOperationException ("Decoder ran into
            // invalid data"), and IOException covers a stream that fails underneath them.
            return (Trim(buffer, length), true);
        }

        static byte[] Trim(byte[] buffer, int length) => length == buffer.Length ? buffer : buffer[..length];
    }

    // The inspector reads a message's decoded body several times per selection, and holding the arrow
    // key made each row cost a decompression per read. A body is replaced, never written in place, so
    // its decoded form holds for as long as the message keeps the same array and the same
    // Content-Encoding. Keyed weakly on the array, the table keeps no released body alive.
    private static readonly ConditionalWeakTable<byte[], CachedDecode> DecodeCache = new();

    // Decoded bodies outlive the read that made them, for as long as their session does, so the cache is
    // held to a budget of its own: only a body that decoded to at most MaxCachedEntryBytes is kept, and
    // once MaxCachedTotalBytes are held the oldest entries make room for new ones. A body that misses
    // the cache is simply decoded again.
    private const int MaxCachedEntryBytes = 8 * 1024 * 1024;
    private const long MaxCachedTotalBytes = 64L * 1024 * 1024;
    private static long _cachedBytes;

    // Guards admission and eviction (not reads). EvictionOrder lists cached entries oldest first;
    // it holds them weakly, so an entry whose body was collected is simply skipped.
    private static readonly object CacheLock = new();
    // A stale entry replaced after its Content-Encoding changed leaves a dead reference behind until an
    // eviction walks past it; that takes an edit of a message's headers per leftover (~48 bytes), which
    // captured traffic cannot cause.
    private static readonly Queue<WeakReference<CachedDecode>> EvictionOrder = new();

    /// <summary>Bytes of decoded bodies the cache holds now; read by the tests that pin its budget.</summary>
    internal static long CachedDecodeBytes => Volatile.Read(ref _cachedBytes);

    /// <summary>
    /// <see cref="DecodeBounded"/> for a message's body, remembered against that array so reading it
    /// again is free. Callers that sweep many bodies once (the search index) use
    /// <see cref="Decode"/>, which leaves the cache to the bodies somebody looks at.
    /// </summary>
    public static DecodedContent DecodeCached(byte[] body, string? contentEncoding)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length == 0 || string.IsNullOrWhiteSpace(contentEncoding)) return new DecodedContent(body, false);

        if (DecodeCache.TryGetValue(body, out var hit))
        {
            if (string.Equals(hit.ContentEncoding, contentEncoding, StringComparison.Ordinal)) return hit.Content;

            // The header changed since this was cached: drop the stale entry and give its budget back.
            Remove(body, hit);
        }

        var content = DecodeBounded(body, contentEncoding);
        var size = ReferenceEquals(content.Bytes, body) ? 0 : content.Bytes.LongLength;
        if (size <= MaxCachedEntryBytes) Admit(body, new CachedDecode(body, contentEncoding, content, size));
        return content;
    }

    /// <summary>
    /// Puts <paramref name="entry"/> in the cache, first evicting the oldest entries until it fits, so
    /// the cache keeps serving what was decoded most recently however long the capture runs. Eviction
    /// is oldest-first (not least-recently-used), which keeps a hit lock-free.
    /// </summary>
    private static void Admit(byte[] body, CachedDecode entry)
    {
        lock (CacheLock)
        {
            // Nothing is held for a body that decoded to itself, so it needs neither room nor a place in
            // the eviction order.
            if (entry.Size > 0)
            {
                while (EvictionOrder.TryPeek(out var head) && !head.TryGetTarget(out _)) EvictionOrder.Dequeue();

                while (Volatile.Read(ref _cachedBytes) + entry.Size > MaxCachedTotalBytes && EvictionOrder.TryDequeue(out var oldest))
                    if (oldest.TryGetTarget(out var victim)) Remove(victim.Key, victim);

                if (Volatile.Read(ref _cachedBytes) + entry.Size > MaxCachedTotalBytes) return;
                Interlocked.Add(ref _cachedBytes, entry.Size);
                entry.Reserved = true;
            }

            // Two threads can decode the same body at once; the loser hands its budget straight back.
            if (!DecodeCache.TryAdd(body, entry)) entry.Release();
            else if (entry.Size > 0) EvictionOrder.Enqueue(new WeakReference<CachedDecode>(entry));
        }
    }

    /// <summary>Takes <paramref name="entry"/> out of the cache, if it is still the one held for its body.</summary>
    private static void Remove(byte[]? body, CachedDecode entry)
    {
        if (body is not null && DecodeCache.TryGetValue(body, out var current) && ReferenceEquals(current, entry))
            DecodeCache.Remove(body);
        entry.Release();
    }

    /// <summary>
    /// A cached decode. It holds its body only weakly, so the cache never keeps a released body alive,
    /// and it owns <see cref="Size"/> bytes of the budget once <see cref="Reserved"/>, which go back
    /// when it is evicted, replaced, or finalised after its body was collected.
    /// </summary>
    private sealed class CachedDecode(byte[] body, string contentEncoding, DecodedContent content, long size)
    {
        private readonly WeakReference<byte[]> _body = new(body);
        private int _released;

        public string ContentEncoding { get; } = contentEncoding;

        public DecodedContent Content { get; } = content;

        public long Size { get; } = size;

        /// <summary>Set, under the cache lock, once the entry has been charged to the budget.</summary>
        public bool Reserved { get; set; }

        /// <summary>The body this was cached for, or null once it has been collected.</summary>
        public byte[]? Key => _body.TryGetTarget(out var key) ? key : null;

        /// <summary>Returns the entry's budget, once, however many paths get here.</summary>
        public void Release()
        {
            GC.SuppressFinalize(this);
            if (Reserved && Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Add(ref _cachedBytes, -Size);
        }

        // Runs once the body this was cached for is gone, returning its share of the budget.
        ~CachedDecode()
        {
            if (Reserved && Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Add(ref _cachedBytes, -Size);
        }
    }

    /// <summary>Resolves the charset from a Content-Type value, defaulting to UTF-8.</summary>
    public static Encoding CharsetFor(string? contentType)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var value = contentType[(idx + 8)..].Trim().Trim('"');
                var end = value.IndexOf(';');
                if (end >= 0) value = value[..end];
                value = value.Trim().Trim('"');
                try
                {
                    if (value.Length > 0) return Encoding.GetEncoding(value);
                }
                catch (ArgumentException)
                {
                    // Unrecognised charset label - fall through to UTF-8.
                }
            }
        }
        return Utf8NoBom;
    }

    /// <summary>Heuristic: is this payload safe to show in a text view?</summary>
    public static bool LooksTextual(string? contentType, byte[] body)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("graphql", StringComparison.OrdinalIgnoreCase)) return true;
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("font", StringComparison.OrdinalIgnoreCase)) return false;
        }

        // Sniff: treat as binary if there are NUL bytes in the first block.
        var probe = Math.Min(body.Length, 1024);
        for (var i = 0; i < probe; i++)
            if (body[i] == 0) return false;
        return true;
    }
}
