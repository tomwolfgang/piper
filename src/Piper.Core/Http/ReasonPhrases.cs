namespace Piper.Core.Http;

/// <summary>
/// Standard reason phrases by status code.
/// </summary>
/// <remarks>
/// Cosmetic in every case Piper uses it: HTTP/2 and HTTP/3 carry no reason phrase at all
/// (RFC 9113 section 8.3.2), and a locally-generated response only needs one so the start line
/// renders sensibly in the inspector.
/// </remarks>
public static class ReasonPhrases
{
    private static readonly Dictionary<int, string> Phrases = new()
    {
        [100] = "Continue", [101] = "Switching Protocols",
        [200] = "OK", [201] = "Created", [202] = "Accepted", [204] = "No Content", [206] = "Partial Content",
        [301] = "Moved Permanently", [302] = "Found", [303] = "See Other", [304] = "Not Modified",
        [307] = "Temporary Redirect", [308] = "Permanent Redirect",
        [400] = "Bad Request", [401] = "Unauthorized", [403] = "Forbidden", [404] = "Not Found",
        [405] = "Method Not Allowed", [406] = "Not Acceptable", [408] = "Request Timeout", [409] = "Conflict",
        [410] = "Gone", [412] = "Precondition Failed", [413] = "Payload Too Large", [414] = "URI Too Long",
        [415] = "Unsupported Media Type", [418] = "I'm a teapot", [429] = "Too Many Requests",
        [500] = "Internal Server Error", [501] = "Not Implemented", [502] = "Bad Gateway",
        [503] = "Service Unavailable", [504] = "Gateway Timeout",
    };

    /// <summary>The phrase for <paramref name="status"/>, or <paramref name="fallback"/> when it is not a listed code.</summary>
    public static string For(int status, string fallback = "") =>
        Phrases.TryGetValue(status, out var text) ? text : fallback;

    /// <summary>Never blank: an unlisted code falls back to the name of its status class.</summary>
    public static string ForOrClass(int status) => For(status, status switch
    {
        >= 100 and < 200 => "Informational",
        >= 200 and < 300 => "OK",
        >= 300 and < 400 => "Redirection",
        >= 400 and < 500 => "Client Error",
        >= 500 and < 600 => "Server Error",
        _ => "Unknown",
    });
}
