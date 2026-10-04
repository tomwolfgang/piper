using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace Piper.Core.Text;

/// <summary>
/// Finds compact JSON Web Tokens in text and validates the parts that can be checked without an issuer's
/// key. This is deliberately an inspector, not an authentication mechanism: a syntactically sound token
/// says nothing about whether its signature, issuer, audience, or lifetime would be accepted by a service.
/// </summary>
internal static class JwtInspector
{
    /// <summary>
    /// A pasted header can contain many JWT-looking values. Capping the result keeps an attacker-controlled
    /// input from expanding into an unbounded report while still making every normal Authorization value
    /// visible.
    /// </summary>
    private const int MaxTokens = 100;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    /// <summary>Inspects every compact-JWT-shaped value in <paramref name="input"/>.</summary>
    public static JwtInspection Inspect(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var tokens = new List<JwtTokenInspection>();
        var position = 0;
        var truncated = false;
        while (TryFindNext(input, ref position, out var candidate))
        {
            if (tokens.Count == MaxTokens)
            {
                truncated = true;
                break;
            }

            tokens.Add(Inspect(
                input[candidate.HeaderStart..candidate.HeaderEnd],
                input[candidate.PayloadStart..candidate.PayloadEnd],
                input[candidate.SignatureStart..candidate.SignatureEnd]));
        }

        return new JwtInspection(tokens, truncated);
    }

    /// <summary>True when text contains at least one compact-JWT-shaped value, without decoding it.</summary>
    public static bool ContainsToken(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var position = 0;
        return TryFindNext(input, ref position, out _);
    }

    private static JwtTokenInspection Inspect(string headerSegment, string payloadSegment, string signatureSegment)
    {
        string? header = null;
        string? algorithm = null;
        JwtValidationError? headerError = null;
        if (!TryDecodeBase64Url(headerSegment, out var headerBytes))
        {
            headerError = JwtValidationError.InvalidHeaderEncoding;
        }
        else if (!TryReadJsonObject(headerBytes, out header, out algorithm, out var parseHeaderError, readAlgorithm: true))
        {
            headerError = parseHeaderError;
        }
        else if (string.IsNullOrWhiteSpace(algorithm))
        {
            headerError = JwtValidationError.MissingAlgorithm;
        }

        string? payload = null;
        JwtValidationError? payloadError = null;
        if (!TryDecodeBase64Url(payloadSegment, out var payloadBytes))
        {
            payloadError = JwtValidationError.InvalidPayloadEncoding;
        }
        else if (!TryReadJsonObject(payloadBytes, out payload, out var parsePayloadError, readAlgorithm: false))
        {
            payloadError = parsePayloadError;
        }

        JwtValidationError? signatureError = null;
        if (!TryDecodeBase64Url(signatureSegment, out _))
        {
            signatureError = JwtValidationError.InvalidSignatureEncoding;
        }
        else if (headerError is null)
        {
            var unsecured = string.Equals(algorithm, "none", StringComparison.Ordinal);
            if (unsecured && signatureSegment.Length != 0)
                signatureError = JwtValidationError.UnexpectedSignature;
            else if (!unsecured && signatureSegment.Length == 0)
                signatureError = JwtValidationError.MissingSignature;
        }

        return new JwtTokenInspection(header, headerError, payload, payloadError, signatureError, algorithm);
    }

    private static bool TryReadJsonObject(byte[] bytes, out string? json, out string? algorithm,
        out JwtValidationError error, bool readAlgorithm)
    {
        json = null;
        algorithm = null;
        error = readAlgorithm ? JwtValidationError.InvalidHeaderJson : JwtValidationError.InvalidPayloadJson;
        try
        {
            using var document = JsonDocument.Parse(bytes, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = readAlgorithm ? JwtValidationError.HeaderIsNotObject : JwtValidationError.PayloadIsNotObject;
                return false;
            }

            json = document.RootElement.GetRawText();
            if (readAlgorithm
                && document.RootElement.TryGetProperty("alg", out var alg)
                && alg.ValueKind == JsonValueKind.String)
            {
                algorithm = alg.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadJsonObject(byte[] bytes, out string? json, out JwtValidationError error,
        bool readAlgorithm) =>
        TryReadJsonObject(bytes, out json, out _, out error, readAlgorithm);

    /// <summary>
    /// JWT compact serialization uses canonical, unpadded base64url. Checking the re-encoded text rejects
    /// invalid trailing bits that <see cref="Convert.FromBase64String(string)"/> would otherwise accept.
    /// </summary>
    private static bool TryDecodeBase64Url(string segment, out byte[] bytes)
    {
        bytes = [];
        if (segment.Length % 4 == 1) return false;

        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };

        try
        {
            bytes = Convert.FromBase64String(padded);
            return string.Equals(Base64Url.EncodeToString(bytes), segment, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int ReadCandidateSegment(string input, int start)
    {
        var position = start;
        while (position < input.Length && IsCandidateSegmentCharacter(input[position])) position++;
        return position;
    }

    /// <summary>
    /// Locates the next complete three-segment value without allocating or decoding it. In particular, a
    /// long run with no dots advances in one pass, which keeps automatic transform detection linear.
    /// </summary>
    private static bool TryFindNext(string input, ref int position, out JwtCandidate candidate)
    {
        while (position < input.Length)
        {
            if (!IsBase64UrlCharacter(input[position])
                || (position > 0 && IsTokenCharacter(input[position - 1])))
            {
                position++;
                continue;
            }

            var headerStart = position;
            var headerEnd = ReadCandidateSegment(input, headerStart);
            if (headerEnd == input.Length || input[headerEnd] != '.')
            {
                position = headerEnd;
                continue;
            }

            var payloadStart = headerEnd + 1;
            if (payloadStart == input.Length || !IsBase64UrlCharacter(input[payloadStart]))
            {
                position = payloadStart;
                continue;
            }

            var payloadEnd = ReadCandidateSegment(input, payloadStart);
            if (payloadEnd == input.Length || input[payloadEnd] != '.')
            {
                position = payloadEnd;
                continue;
            }

            var signatureStart = payloadEnd + 1;
            var signatureEnd = ReadCandidateSegment(input, signatureStart);
            if (signatureEnd < input.Length && IsTokenCharacter(input[signatureEnd]))
            {
                // A fourth segment (or an unseparated suffix) is not a compact JWT. Treat the entire run as
                // one malformed value rather than accidentally accepting one of its three-segment substrings.
                position = signatureEnd;
                continue;
            }

            candidate = new JwtCandidate(headerStart, headerEnd, payloadStart, payloadEnd, signatureStart, signatureEnd);
            position = signatureEnd;
            return true;
        }

        candidate = default;
        return false;
    }

    private static bool IsTokenCharacter(char value) => value == '.' || IsBase64UrlCharacter(value);

    private static bool IsBase64UrlCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

    // This wider character set makes a padded or standard-base64 segment show up as an invalid JWT rather
    // than letting its valid-looking prefix be reported as a valid one. The strict decoder above remains
    // the authority on what a real compact JWT may contain.
    private static bool IsCandidateSegmentCharacter(char value) =>
        IsBase64UrlCharacter(value) || value is '+' or '/' or '=';

    private readonly record struct JwtCandidate(int HeaderStart, int HeaderEnd, int PayloadStart, int PayloadEnd,
        int SignatureStart, int SignatureEnd);
}

/// <summary>The bounded result of inspecting compact JWT values in one TextWizard input.</summary>
public sealed record JwtInspection(IReadOnlyList<JwtTokenInspection> Tokens, bool IsTruncated)
{
    /// <summary>
    /// Serializes the decoded header and payload values as JSON. Invalid parts become null so the result
    /// remains valid JSON and can be sent back to the TextWizard input without carrying display text.
    /// </summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            if (Tokens.Count == 1)
            {
                WriteToken(writer, Tokens[0]);
            }
            else
            {
                writer.WriteStartArray();
                foreach (var token in Tokens) WriteToken(writer, token);
                writer.WriteEndArray();
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteToken(Utf8JsonWriter writer, JwtTokenInspection token)
    {
        writer.WriteStartObject();
        WritePart(writer, "header", token.Header);
        WritePart(writer, "payload", token.Payload);
        writer.WriteEndObject();
    }

    private static void WritePart(Utf8JsonWriter writer, string name, string? json)
    {
        writer.WritePropertyName(name);
        if (json is null)
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(json);
        document.RootElement.WriteTo(writer);
    }
}

/// <summary>The independently decoded parts and structural validation errors for one compact JWT.</summary>
public sealed record JwtTokenInspection(string? Header, JwtValidationError? HeaderError, string? Payload,
    JwtValidationError? PayloadError, JwtValidationError? SignatureError, string? Algorithm)
{
    /// <summary>True when every compact-JWT part is structurally valid.</summary>
    public bool IsValid => HeaderError is null && PayloadError is null && SignatureError is null;

    /// <summary>The first error, retained for callers that only need an overall verdict.</summary>
    public JwtValidationError? Error => HeaderError ?? PayloadError ?? SignatureError;
}

/// <summary>Reasons a compact JWT failed structural validation, kept separate from localized presentation.</summary>
public enum JwtValidationError
{
    InvalidHeaderEncoding,
    InvalidHeaderJson,
    HeaderIsNotObject,
    MissingAlgorithm,
    InvalidPayloadEncoding,
    InvalidPayloadJson,
    PayloadIsNotObject,
    InvalidSignatureEncoding,
    MissingSignature,
    UnexpectedSignature,
}
