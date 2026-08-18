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
            runner.AreEqual("aGVsbG8=", Apply(TextTransform.ToBase64, "hello"), "base64 keeps its padding");
            runner.AreEqual("aGVsbG8", Apply(TextTransform.ToBase64Url, "hello"), "base64url strips padding");
            runner.AreEqual("4869", Apply(TextTransform.HexEncode, "Hi"), "hex is uppercase and unspaced");
            runner.AreEqual("\"a\\\"b\"", Apply(TextTransform.ToJsString, "a\"b"), "a quote is escaped inside the literal");
            runner.AreEqual("new byte[] { 0x48, 0x69 }", Apply(TextTransform.ToCSharpByteArray, "Hi"), "a C# byte[] literal");
            runner.AreEqual("+AOk-", Apply(TextTransform.ToUtf7, "é"), "UTF-7 escapes non-ASCII into ASCII");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard decoders read what a server would send", () =>
        {
            runner.AreEqual("a b", Apply(TextTransform.UrlDecode, "a+b"), "a query-string plus decodes to a space");
            runner.AreEqual("a b", Apply(TextTransform.UrlDecode, "a%20b"), "a percent escape decodes to a space");
            runner.AreEqual("<b>&", Apply(TextTransform.HtmlDecode, "&lt;b&gt;&amp;"), "entities decode back to markup");
            runner.AreEqual("hello", Apply(TextTransform.FromBase64, "aGVsbG8="), "padded base64 decodes");
            runner.AreEqual("Hi", Apply(TextTransform.HexDecode, "4869"), "hex decodes");
            runner.AreEqual("a\"b", Apply(TextTransform.FromJsString, "\"a\\\"b\""), "a JS string literal decodes");
            runner.AreEqual("é", Apply(TextTransform.FromUtf7, "+AOk-"), "UTF-7 decodes back to non-ASCII");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard base64 accepts the shapes that turn up in real traffic", () =>
        {
            // Fiddler's From Base64 is equally forgiving; a strict decoder would reject most JWT segments.
            runner.AreEqual("hello", Apply(TextTransform.FromBase64, "aGVsbG8"), "missing padding is tolerated");
            runner.AreEqual("{\"alg\":\"HS256\",\"typ\":\"JWT\"}",
                Apply(TextTransform.FromBase64, "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"), "a JWT header segment decodes");
            runner.AreEqual("hello", Apply(TextTransform.FromBase64, "aGVs bG8\r\n"), "wrapped and spaced base64 decodes");
            runner.AreEqual(Apply(TextTransform.FromBase64, "++//"), Apply(TextTransform.FromBase64, "--__"),
                "the URL-safe alphabet decodes the same as the standard one");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard round-trips text that carries separators and non-ASCII", () =>
        {
            const string sample = "café ✓ a+b&c=d \"q\" <tag>\nline";
            foreach (var (encode, decode) in new[]
                     {
                         (TextTransform.UrlEncode, TextTransform.UrlDecode),
                         (TextTransform.HtmlEncode, TextTransform.HtmlDecode),
                         (TextTransform.ToBase64, TextTransform.FromBase64),
                         (TextTransform.ToBase64Url, TextTransform.FromBase64),
                         (TextTransform.HexEncode, TextTransform.HexDecode),
                         (TextTransform.ToJsString, TextTransform.FromJsString),
                         (TextTransform.ToUtf7, TextTransform.FromUtf7),
                         (TextTransform.ToDeflatedSaml, TextTransform.FromDeflatedSaml),
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
            runner.AreEqual(96, Apply(TextTransform.Sha384, "abc").Length, "SHA-384 produces 48 bytes of hex");
            runner.AreEqual(128, Apply(TextTransform.Sha512, "abc").Length, "SHA-512 produces 64 bytes of hex");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard decoders reject malformed input instead of guessing", () =>
        {
            Throws<FormatException>(runner, TextTransform.FromBase64, "not base64!!", "base64 with illegal characters");
            Throws<FormatException>(runner, TextTransform.FromBase64, "aGVsbG8yZQ==x", "base64 with a stray trailing character");
            Throws<FormatException>(runner, TextTransform.HexDecode, "486", "hex of odd length");
            Throws<FormatException>(runner, TextTransform.HexDecode, "48ZZ", "hex with a non-hex digit");
            Throws<JsonException>(runner, TextTransform.FromJsString, "not a literal", "an unquoted JS string");
            Throws<JsonException>(runner, TextTransform.FromJsString, "123", "a JS number where a string was expected");
            Throws<InvalidDataException>(runner, TextTransform.FromDeflatedSaml, "aGVsbG8=", "base64 that is not DEFLATE data");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard bounds a decompression bomb instead of exhausting memory", () =>
        {
            // 8 MiB of zeroes deflates to a couple of KiB. Inflating it must stop at the 1 MiB cap.
            var bomb = Apply(TextTransform.ToDeflatedSaml, new string('\0', 8 * 1024 * 1024));
            runner.IsTrue(bomb.Length < 64 * 1024, "the bomb really is small while compressed");
            Throws<InvalidDataException>(runner, TextTransform.FromDeflatedSaml, bomb, "an oversized expansion");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard shows undecodable bytes rather than failing", () =>
        {
            // 0xFF is not valid UTF-8; the byte still has to surface somewhere the user can see it.
            runner.AreEqual("�", Apply(TextTransform.FromBase64, "/w=="), "an invalid byte becomes the replacement character");
            runner.AreEqual(string.Empty, Apply(TextTransform.FromBase64, string.Empty), "empty input stays empty");
            runner.AreEqual(string.Empty, Apply(TextTransform.UrlEncode, string.Empty), "an empty encode is not an error");
            runner.AreEqual("new byte[] { }", Apply(TextTransform.ToCSharpByteArray, string.Empty),
                "an empty C# literal is still valid C#");
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
            var shown = output.Length > 40 ? output[..40] + "..." : output;
            runner.IsTrue(false, $"{what} is rejected (got <{shown}>)");
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
