using System.Text.Json;
using Piper.Core.Text;

internal static class TextTransformsTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("TextWizard encoders produce the expected wire text", () =>
        {
            runner.AreEqual("a%20b%26c%3Dd", Apply(TextTransform.UrlEncode, "a b&c=d"), "reserved characters are escaped");
            runner.AreEqual("&lt;b&gt;&amp;", Apply(TextTransform.HtmlEncode, "<b>&"), "markup is escaped");
            runner.AreEqual("aGVsbG8=", Apply(TextTransform.Base64Encode, "hello"), "base64 keeps its padding");
            runner.AreEqual("aGVsbG8", Apply(TextTransform.Base64UrlEncode, "hello"), "base64url strips padding");
            runner.AreEqual("4869", Apply(TextTransform.HexEncode, "Hi"), "hex is uppercase and unspaced");
            runner.AreEqual("\"a\\\"b\"", Apply(TextTransform.JsonStringEncode, "a\"b"), "a quote is escaped inside the literal");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard decoders read what a server would send", () =>
        {
            runner.AreEqual("a b", Apply(TextTransform.UrlDecode, "a+b"), "a query-string plus decodes to a space");
            runner.AreEqual("a b", Apply(TextTransform.UrlDecode, "a%20b"), "a percent escape decodes to a space");
            runner.AreEqual("<b>&", Apply(TextTransform.HtmlDecode, "&lt;b&gt;&amp;"), "entities decode back to markup");
            runner.AreEqual("hello", Apply(TextTransform.Base64Decode, "aGVsbG8="), "padded base64 decodes");
            runner.AreEqual("hello", Apply(TextTransform.Base64UrlDecode, "aGVsbG8"), "base64url decodes with padding restored");
            runner.AreEqual("{\"alg\":\"HS256\",\"typ\":\"JWT\"}",
                Apply(TextTransform.Base64UrlDecode, "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"), "a JWT header segment decodes");
            runner.AreEqual("Hi", Apply(TextTransform.HexDecode, "4869"), "hex decodes");
            runner.AreEqual("a\"b", Apply(TextTransform.JsonStringDecode, "\"a\\\"b\""), "a JSON string literal decodes");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard round-trips text that carries separators and non-ASCII", () =>
        {
            const string sample = "café ✓ a+b&c=d \"q\" <tag>\nline";
            foreach (var (encode, decode) in new[]
                     {
                         (TextTransform.UrlEncode, TextTransform.UrlDecode),
                         (TextTransform.HtmlEncode, TextTransform.HtmlDecode),
                         (TextTransform.Base64Encode, TextTransform.Base64Decode),
                         (TextTransform.Base64UrlEncode, TextTransform.Base64UrlDecode),
                         (TextTransform.HexEncode, TextTransform.HexDecode),
                         (TextTransform.JsonStringEncode, TextTransform.JsonStringDecode),
                     })
            {
                runner.AreEqual(sample, Apply(decode, Apply(encode, sample)), $"{encode} survives {decode}");
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard hashes match published vectors", () =>
        {
            runner.AreEqual("D41D8CD98F00B204E9800998ECF8427E", Apply(TextTransform.Md5, string.Empty), "MD5 of the empty string");
            runner.AreEqual("A9993E364706816ABA3E25717850C26C9CD0D89D", Apply(TextTransform.Sha1, "abc"), "SHA-1 of abc");
            runner.AreEqual("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                Apply(TextTransform.Sha256, "abc"), "SHA-256 of abc");
            runner.AreEqual(128, Apply(TextTransform.Sha512, "abc").Length, "SHA-512 produces 64 bytes of hex");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard decoders reject malformed input instead of guessing", () =>
        {
            Throws<FormatException>(runner, TextTransform.Base64Decode, "not base64!!", "base64 with illegal characters");
            Throws<FormatException>(runner, TextTransform.Base64Decode, "aGVsbG8", "base64 missing its padding");
            Throws<FormatException>(runner, TextTransform.Base64UrlDecode, "aGVsb!8", "base64url with an illegal character");
            Throws<FormatException>(runner, TextTransform.HexDecode, "486", "hex of odd length");
            Throws<FormatException>(runner, TextTransform.HexDecode, "48ZZ", "hex with a non-hex digit");
            Throws<JsonException>(runner, TextTransform.JsonStringDecode, "not a literal", "an unquoted JSON string");
            Throws<JsonException>(runner, TextTransform.JsonStringDecode, "123", "a JSON number where a string was expected");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard shows undecodable bytes rather than failing", () =>
        {
            // 0xFF is not valid UTF-8; the byte still has to surface somewhere the user can see it.
            runner.AreEqual("�", Apply(TextTransform.Base64Decode, "/w=="), "an invalid byte becomes the replacement character");
            runner.AreEqual(string.Empty, Apply(TextTransform.Base64Decode, string.Empty), "empty input stays empty");
            runner.AreEqual(string.Empty, Apply(TextTransform.UrlEncode, string.Empty), "an empty encode is not an error");
            return Task.CompletedTask;
        });
    }

    private static string Apply(TextTransform transform, string input) => TextTransforms.Apply(transform, input);

    private static void Throws<TException>(TestRunner runner, TextTransform transform, string input, string what)
        where TException : Exception
    {
        try
        {
            var output = TextTransforms.Apply(transform, input);
            runner.IsTrue(false, $"{what} is rejected (got <{output}>)");
        }
        catch (TException)
        {
            runner.IsTrue(true, $"{what} is rejected");
        }
        catch (Exception ex)
        {
            runner.IsTrue(false, $"{what} is rejected with {typeof(TException).Name} (got {ex.GetType().Name})");
        }
    }
}
