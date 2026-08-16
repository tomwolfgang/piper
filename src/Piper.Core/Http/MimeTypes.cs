namespace Piper.Core.Http;

/// <summary>
/// Content types by file extension, for responses Piper serves from disk.
/// </summary>
/// <remarks>
/// Hand-maintained rather than taken from a package: Piper.Core has no NuGet dependencies, and the
/// only alternative in the framework lives in ASP.NET Core. The list covers what an AutoResponder
/// rule realistically serves; anything unlisted is sent as application/octet-stream, which browsers
/// download rather than misinterpret.
/// </remarks>
public static class MimeTypes
{
    public const string Default = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html", [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript", [".mjs"] = "text/javascript",
        [".json"] = "application/json", [".map"] = "application/json",
        [".xml"] = "application/xml", [".xhtml"] = "application/xhtml+xml",
        [".txt"] = "text/plain", [".md"] = "text/markdown", [".csv"] = "text/csv",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif",
        [".bmp"] = "image/bmp", [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip", [".gz"] = "application/gzip",
        [".wasm"] = "application/wasm",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".mp4"] = "video/mp4", [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
    };

    /// <summary>Types served with an explicit UTF-8 charset, so a browser does not guess the encoding.</summary>
    private static readonly HashSet<string> Textual = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "text/css", "text/javascript", "text/plain", "text/markdown", "text/csv",
        "application/json", "application/xml", "application/xhtml+xml", "image/svg+xml",
    };

    public static string ForFile(string? path) => ForExtension(Path.GetExtension(path ?? string.Empty));

    public static string ForExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return Default;
        if (extension[0] != '.') extension = "." + extension;
        if (!ByExtension.TryGetValue(extension, out var type)) return Default;

        return Textual.Contains(type) ? $"{type}; charset=utf-8" : type;
    }
}
