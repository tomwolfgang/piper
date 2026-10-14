namespace Piper.Core.Http;

/// <summary>
/// Collects a message body into one array sized by the bytes that have arrived, not by the bytes a
/// peer says it will send.
/// </summary>
/// <remarks>
/// A Content-Length or chunk size is only a claim. Allocating it up front hands a client that
/// declares a large body and then sends nothing that much committed memory for as long as it holds
/// the connection open. Here the array starts small and at most quadruples each time it fills, so what
/// is committed stays within about four times what was received. A declared total is a ceiling for
/// growth until it is reached, so a body that arrives in full ends in an array of exactly its length
/// and <see cref="ToArray"/> hands it over without a copy. The caller enforces the size limit.
/// </remarks>
internal sealed class BodyBuffer
{
    /// <summary>The first allocation: the most a peer that sends nothing can make us commit.</summary>
    internal const int InitialCapacity = 64 * 1024;

    private readonly int _expected; // 0 when the total is not known
    private readonly int _ceiling;
    private byte[] _array;

    /// <param name="expectedLength">The declared total, or 0 when it is not known.</param>
    /// <param name="ceiling">The most the array may ever hold (the caller's size limit): no growth step
    /// goes past it, so the memory committed never exceeds the limit even for a body of unknown length.</param>
    public BodyBuffer(int expectedLength = 0, int ceiling = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        ArgumentOutOfRangeException.ThrowIfNegative(ceiling);
        _expected = expectedLength;
        _ceiling = Math.Min(ceiling, Array.MaxLength);
        _array = expectedLength == 0 ? [] : Allocate(Math.Min(expectedLength, InitialCapacity));
    }

    /// <summary>How many body bytes have been received.</summary>
    public int Length { get; private set; }

    /// <summary>Room to read into, at most <paramref name="atMost"/> bytes of it. Grows the array when full.</summary>
    public Memory<byte> Reserve(int atMost)
    {
        if (atMost <= 0) return Memory<byte>.Empty;
        if (Length == _array.Length) Grow();
        return _array.AsMemory(Length, Math.Min(_array.Length - Length, atMost));
    }

    /// <summary>Records that <paramref name="count"/> bytes were written into what <see cref="Reserve"/> returned.</summary>
    public void Advance(int count) => Length += count;

    /// <summary>Adds bytes that are already in hand, such as the payload of a DATA frame.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var room = Reserve(data.Length);
            data[..room.Length].CopyTo(room.Span);
            Advance(room.Length);
            data = data[room.Length..];
        }
    }

    /// <summary>Hands over the body and empties this buffer: the array itself when exactly full, else a right-sized copy.</summary>
    public byte[] ToArray()
    {
        var array = _array;
        var length = Length;
        Clear();
        if (length == 0) return [];
        return length == array.Length ? array : array.AsSpan(0, length).ToArray();
    }

    /// <summary>Forgets what has been received and lets go of the memory holding it.</summary>
    public void Clear()
    {
        _array = [];
        Length = 0;
    }

    private void Grow()
    {
        long next;
        if (_expected > _array.Length)
        {
            // Steps of at most 4x, counted back from the declared total so that the last one lands on it
            // exactly (and the arrays left behind stay few and small next to the body).
            next = _expected;
            while (next / 4 > _array.Length) next /= 4;
        }
        else
        {
            // Unknown total, or more than was declared (a peer may send more than it said it would).
            next = Math.Max((long)InitialCapacity, (long)_array.Length * 4);
        }
        next = Math.Min(next, _ceiling);
        if (next <= _array.Length) throw new HttpBodyTooLargeException("Body would not fit in one array.");

        var bigger = Allocate((int)next);
        _array.AsSpan(0, Length).CopyTo(bigger);
        _array = bigger;
    }

    // Uninitialised: only [0, Length) is ever read, and an array handed out by ToArray is full.
    private static byte[] Allocate(int size) => GC.AllocateUninitializedArray<byte>(size);
}
