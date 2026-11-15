namespace Piper.Core.Text;

/// <summary>Reads a whole stream into memory without ever trusting its length.</summary>
public static class BoundedRead
{
    /// <summary>Reads at most <paramref name="maxBytes"/>; false when the stream holds more.</summary>
    public static bool TryRead(Stream stream, long maxBytes, out byte[] content, out int length)
    {
        content = [];
        length = 0;

        // The length is checked while reading rather than up front: it is only a hint, and a file
        // that grows after it was asked for must not be read past the cap.
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var remaining = maxBytes + 1;
        int read;
        while (remaining > 0 && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining))) > 0)
        {
            buffer.Write(chunk, 0, read);
            remaining -= read;
        }

        if (buffer.Length > maxBytes) return false;
        content = buffer.GetBuffer();
        length = (int)buffer.Length;
        return true;
    }
}
