namespace Piper.App.Controls;

/// <summary>Display formatting shared by the grid, the inspector and the composer.</summary>
internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "-",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024):N2} MB",
    };

    /// <summary>Trims "application/json; charset=utf-8" down to "json" for grid display.</summary>
    public static string ShortContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return string.Empty;

        var value = contentType;
        var semi = value.IndexOf(';');
        if (semi > 0) value = value[..semi];
        value = value.Trim();

        var slash = value.IndexOf('/');
        if (slash < 0) return value;

        var type = value[..slash];
        var subtype = value[(slash + 1)..];

        // "application/vnd.api+json" reads better as "json".
        var plus = subtype.LastIndexOf('+');
        if (plus > 0) subtype = subtype[(plus + 1)..];

        return type is "application" ? subtype : $"{type}/{subtype}";
    }
}
