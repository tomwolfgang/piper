using Piper.Core.Http;

namespace Piper.App.Controls;

/// <summary>Display formatting shared by the grid, the inspector and the composer.</summary>
internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        <= 0 => Strings.Units.NoBytes,
        < 1024 => Strings.Units.Bytes(bytes),
        < 1024 * 1024 => Strings.Units.Kilobytes(bytes / 1024.0),
        _ => Strings.Units.Megabytes(bytes / (1024.0 * 1024)),
    };

    private const double Kilobyte = 1024, Megabyte = Kilobyte * 1024, Gigabyte = Megabyte * 1024;

    // A total moves up a unit before it would print as 1,000 or more: at one decimal 1,023.99 KB
    // rounds to "1,024.0", and "1,023.9/1,024.0 KB" is four characters wider than any other form,
    // which is what the Size column would have to be sized for.
    private const long MaxBytesTotal = 999;
    private const long MaxKilobytesTotal = (long)(999.95 * Kilobyte);
    private const long MaxMegabytesTotal = (long)(999.95 * Megabyte);

    /// <summary>
    /// "3.0/8.0 MB" for a body still arriving, in the unit of its total so the two line up. The
    /// received figure is rounded down, so a body that is not finished never reads as finished.
    /// </summary>
    public static string SizeProgress(long received, long total) => total switch
    {
        <= MaxBytesTotal => Strings.Units.ProgressBytes(received, total),
        < MaxKilobytesTotal => Strings.Units.ProgressKilobytes(Floor(received / Kilobyte, 1), total / Kilobyte),
        < MaxMegabytesTotal => Strings.Units.ProgressMegabytes(Floor(received / Megabyte, 1), total / Megabyte),
        _ => Strings.Units.ProgressGigabytes(Floor(received / Gigabyte, 2), total / Gigabyte),
    };

    /// <summary>
    /// The widest text the Size column shows, so it can be sized never to cut a figure off: the
    /// largest total each unit of <see cref="SizeProgress"/> prints, and a finished body of tens of
    /// gigabytes, which <see cref="Size"/> still counts in megabytes. Totals of a terabyte or more
    /// are wider and are left to the ellipsis.
    /// </summary>
    public static string[] WidestSizeTexts() =>
    [
        SizeProgress(MaxBytesTotal, MaxBytesTotal),
        SizeProgress(MaxKilobytesTotal - 1, MaxKilobytesTotal - 1),
        SizeProgress(MaxMegabytesTotal - 1, MaxMegabytesTotal - 1),
        SizeProgress((long)(999.99 * Gigabyte), (long)(999.99 * Gigabyte)),
        Size(99_999 * (long)Megabyte),
    ];

    /// <summary>Longest a Time cell gets before a body has been arriving for hours: 9,999,999 ms.</summary>
    public static string[] WidestDurationTexts() =>
        [Strings.SessionList.Duration(9_999_999), Strings.SessionList.PendingDuration];

    /// <summary>
    /// The widest Result texts: <see cref="Piper.Core.Sessions.Session.StatusText"/>'s words and a
    /// status code marked as still arriving.
    /// </summary>
    public static string[] WidestResultTexts() => ["CONNECT", "ERR", Strings.SessionList.ReceivingResult("999")];

    /// <summary>"3.02 MB of 8.00 MB (37%)", or just what has arrived when the total is unknown.</summary>
    public static string ProgressDetail(long received, long total) => total > 0
        ? Strings.Units.OfTotal(
            received > 0 ? Size(received) : Strings.Units.Bytes(0), Size(total),
            Floor(Math.Clamp((double)received / total, 0, 1), 2))
        : Size(received);

    private static double Floor(double value, int decimals)
    {
        var scale = Math.Pow(10, decimals);
        return Math.Floor(value * scale) / scale;
    }

    /// <summary>
    /// Shortens a path to at most <paramref name="maxChars"/> by dropping its middle, so a narrow
    /// cell still names the file: "/files/5120/338/create.jar" becomes "/fi.../create.jar". Text that
    /// fits comes back unchanged, and so does text whose last segment would not fit even on its own,
    /// which the cell's end ellipsis then shortens as before.
    /// </summary>
    /// <remarks>
    /// Windows' own path ellipsis only recognises backslashes: given a URL path it clips the text
    /// without an ellipsis at all. The segment is found before any query, whose values may hold "/".
    /// </remarks>
    public static string ShortenPath(string text, int maxChars)
    {
        const string Ellipsis = "...";
        if (text.Length <= maxChars) return text;

        var query = text.IndexOf('?');
        var lastSlash = query < 0 ? text.LastIndexOf('/') : text.LastIndexOf('/', query);
        if (lastSlash <= 0) return text;

        var head = maxChars - Ellipsis.Length - (text.Length - lastSlash);
        return head < 1 ? text : string.Concat(text.AsSpan(0, head), Ellipsis, text.AsSpan(lastSlash));
    }

    /// <summary>Trims "application/json; charset=utf-8" down to "json" for grid display.</summary>
    public static string ShortContentType(string? contentType) => MimeTypes.ShortName(contentType);
}
