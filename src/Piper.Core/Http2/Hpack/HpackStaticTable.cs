namespace Piper.Core.Http2.Hpack;

/// <summary>The 61-entry HPACK static table, RFC 7541 Appendix A. Read-only, shared by every
/// connection; indices are 1-based per the RFC's combined static+dynamic address space.</summary>
public static class HpackStaticTable
{
    private static readonly (string Name, string Value)[] Entries =
    [
        (":authority", ""),
        (":method", "GET"),
        (":method", "POST"),
        (":path", "/"),
        (":path", "/index.html"),
        (":scheme", "http"),
        (":scheme", "https"),
        (":status", "200"),
        (":status", "204"),
        (":status", "206"),
        (":status", "304"),
        (":status", "400"),
        (":status", "404"),
        (":status", "500"),
        ("accept-charset", ""),
        ("accept-encoding", "gzip, deflate"),
        ("accept-language", ""),
        ("accept-ranges", ""),
        ("accept", ""),
        ("access-control-allow-origin", ""),
        ("age", ""),
        ("allow", ""),
        ("authorization", ""),
        ("cache-control", ""),
        ("content-disposition", ""),
        ("content-encoding", ""),
        ("content-language", ""),
        ("content-length", ""),
        ("content-location", ""),
        ("content-range", ""),
        ("content-type", ""),
        ("cookie", ""),
        ("date", ""),
        ("etag", ""),
        ("expect", ""),
        ("expires", ""),
        ("from", ""),
        ("host", ""),
        ("if-match", ""),
        ("if-modified-since", ""),
        ("if-none-match", ""),
        ("if-range", ""),
        ("if-unmodified-since", ""),
        ("last-modified", ""),
        ("link", ""),
        ("location", ""),
        ("max-forwards", ""),
        ("proxy-authenticate", ""),
        ("proxy-authorization", ""),
        ("range", ""),
        ("referer", ""),
        ("refresh", ""),
        ("retry-after", ""),
        ("server", ""),
        ("set-cookie", ""),
        ("strict-transport-security", ""),
        ("transfer-encoding", ""),
        ("user-agent", ""),
        ("vary", ""),
        ("via", ""),
        ("www-authenticate", ""),
    ];

    public static int Count => Entries.Length;

    /// <summary>1-based lookup, matching the RFC's index address space.</summary>
    public static (string Name, string Value) Get(int index) => Entries[index - 1];

    /// <summary>First static-table index whose name matches, for the encoder's "indexed name,
    /// literal value" shortcut. Returns false when no static entry has this header name.</summary>
    public static bool TryFindName(string name, out int index)
    {
        for (var i = 0; i < Entries.Length; i++)
        {
            if (string.Equals(Entries[i].Name, name, StringComparison.Ordinal))
            {
                index = i + 1;
                return true;
            }
        }
        index = 0;
        return false;
    }
}
