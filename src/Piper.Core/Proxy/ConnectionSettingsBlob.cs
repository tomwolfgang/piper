using System.Buffers.Binary;
using System.Text;

namespace Piper.Core.Proxy;

/// <summary>
/// The binary connection settings WinINET stores under Internet Settings\Connections.
/// </summary>
/// <remarks>
/// This blob - not the ProxyEnable/ProxyServer values next to it - is what
/// WinHttpGetIEProxyConfigForCurrentUser returns, which is how Chrome, Edge, WinHTTP clients and
/// .NET pick up "the Windows proxy". Writing only the legacy values leaves those callers pointed
/// at a proxy that is no longer listening, so both have to be kept in step.
///
/// Layout: version, a change counter, flags, then three length-prefixed strings (manual proxy,
/// bypass list, auto-config URL), then a fixed trailer. The trailer is carried through untouched
/// so writing a value back never discards fields Windows put there.
/// </remarks>
public sealed record ConnectionSettingsBlob
{
    public const int DirectFlag = 0x01;
    public const int ProxyFlag = 0x02;
    public const int AutoConfigFlag = 0x04;
    public const int AutoDetectFlag = 0x08;

    private const int CurrentVersion = 0x46;
    private const int HeaderLength = 12;
    private const int CounterOffset = 4;
    private const int TrailerLength = 32;

    /// <summary>What Windows itself writes for a plain "no proxy" configuration.</summary>
    public static ConnectionSettingsBlob Direct { get; } = new();

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Bumped on every write; Windows uses it to spot settings that changed underneath it.</summary>
    public int Counter { get; init; }

    public int Flags { get; init; } = DirectFlag;

    public string ProxyServer { get; init; } = string.Empty;

    public string ProxyBypass { get; init; } = string.Empty;

    public string AutoConfigUrl { get; init; } = string.Empty;

    public byte[] Trailer { get; init; } = new byte[TrailerLength];

    /// <summary>Switches the blob to a manual proxy, keeping any auto-detect or PAC flags as they were.</summary>
    public ConnectionSettingsBlob WithProxy(string endpoint, string bypassList) => this with
    {
        Flags = (Flags & ~DirectFlag) | ProxyFlag,
        ProxyServer = endpoint,
        ProxyBypass = bypassList,
    };

    /// <summary>
    /// Reads a registry value. Anything unrecognised is rejected rather than half-parsed: a wrong
    /// guess here would be written straight back into the user's network configuration.
    /// </summary>
    public static bool TryParse(byte[]? value, out ConnectionSettingsBlob blob)
    {
        blob = Direct;
        if (value is null || value.Length < HeaderLength) return false;

        var span = value.AsSpan();
        var version = BinaryPrimitives.ReadInt32LittleEndian(span);
        var counter = BinaryPrimitives.ReadInt32LittleEndian(span[CounterOffset..]);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);

        var offset = HeaderLength;
        if (!TryReadString(span, ref offset, out var proxyServer)) return false;
        if (!TryReadString(span, ref offset, out var proxyBypass)) return false;
        if (!TryReadString(span, ref offset, out var autoConfigUrl)) return false;

        blob = new ConnectionSettingsBlob
        {
            Version = version,
            Counter = counter,
            Flags = flags,
            ProxyServer = proxyServer,
            ProxyBypass = proxyBypass,
            AutoConfigUrl = autoConfigUrl,
            Trailer = span[offset..].ToArray(),
        };
        return true;
    }

    public byte[] ToBytes()
    {
        var proxyServer = Encoding.UTF8.GetBytes(ProxyServer);
        var proxyBypass = Encoding.UTF8.GetBytes(ProxyBypass);
        var autoConfigUrl = Encoding.UTF8.GetBytes(AutoConfigUrl);

        var bytes = new byte[HeaderLength
            + (3 * sizeof(int)) + proxyServer.Length + proxyBypass.Length + autoConfigUrl.Length
            + Trailer.Length];

        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, Version);
        BinaryPrimitives.WriteInt32LittleEndian(span[CounterOffset..], Counter);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], Flags);

        var offset = HeaderLength;
        WriteString(span, ref offset, proxyServer);
        WriteString(span, ref offset, proxyBypass);
        WriteString(span, ref offset, autoConfigUrl);
        Trailer.CopyTo(span[offset..]);

        return bytes;
    }

    /// <summary>The counter of an existing value, or 0 when there is nothing readable to continue from.</summary>
    public static int ReadCounter(byte[]? value) =>
        value is not null && value.Length >= HeaderLength
            ? BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(CounterOffset))
            : 0;

    /// <summary>
    /// Stamps a new counter onto bytes that are otherwise written back verbatim. Restoring the
    /// captured value with its original counter would look stale next to the one Piper wrote.
    /// </summary>
    public static byte[] WithCounter(byte[] value, int counter)
    {
        var copy = (byte[])value.Clone();
        if (copy.Length >= HeaderLength) BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(CounterOffset), counter);
        return copy;
    }

    private static bool TryReadString(ReadOnlySpan<byte> span, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + sizeof(int) > span.Length) return false;

        var length = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
        offset += sizeof(int);
        // Subtraction rather than offset + length: a hostile or corrupt length would overflow.
        if (length < 0 || length > span.Length - offset) return false;

        value = Encoding.UTF8.GetString(span.Slice(offset, length));
        offset += length;
        return true;
    }

    private static void WriteString(Span<byte> span, ref int offset, byte[] value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], value.Length);
        offset += sizeof(int);
        value.CopyTo(span[offset..]);
        offset += value.Length;
    }
}
