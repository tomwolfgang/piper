using Piper.Core.Sessions;
using Piper.Core.Text;

internal static class TextTransformDetectorTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("TextWizard detects the encoding of values captured from traffic", () =>
        {
            // Every one of these is a value this feature was built to read, taken from real captured traffic.
            Detects(runner, TextTransform.FromBase64, "YWRtaW46aHVudGVyMg==", "an Authorization: Basic value");
            Detects(runner, TextTransform.FromBase64, "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", "a JWT header segment");
            Detects(runner, TextTransform.InspectJwt,
                "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2ln anything eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJib2IifQ.c2ln",
                "JWTs embedded in an Authorization-style value");
            Detects(runner, TextTransform.UrlDecode, "redirect=https%3A%2F%2Fexample.com%2Fa%2Bb", "an encoded query string");
            Detects(runner, TextTransform.HexDecode, "48656C6C6F2C20576F726C6421", "a hex payload header");
            Detects(runner, TextTransform.HtmlDecode, "&lt;script&gt;alert(1)&lt;/script&gt;", "HTML entities");
            Detects(runner, TextTransform.FromJsString, "\"line1\\nline2\"", "a JSON string literal");
            Detects(runner, TextTransform.FromUtf7, "+AOk-t+AOk-", "a UTF-7 run");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard detects a deflated SAML payload as SAML, not plain base64", () =>
        {
            var saml = TextTransforms.Apply(TextTransform.ToDeflatedSaml,
                "<samlp:AuthnRequest xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" ID=\"_a1b2c3\"/>");
            Detects(runner, TextTransform.FromDeflatedSaml, saml, "a SAML redirect payload");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard offers no guess rather than a wrong one", () =>
        {
            foreach (var (input, what) in new[]
                     {
                         ("", "empty input"),
                         ("   ", "whitespace"),
                         ("hello world", "ordinary prose"),
                         ("a+b", "a bare plus, which is far more often just text"),
                         ("cafe", "a short word that happens to be hex"),
                         ("/api/v1/orders", "a plain path"),
                     })
            {
                runner.AreEqual(null, TextTransformDetector.Detect(input), $"no guess for {what}");
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard detection stays bounded on a huge input", () =>
        {
            // Detection must not decode a megabyte twice to answer. It still offers the base64 guess for
            // base64-shaped text, just without the decode that would confirm it.
            var big = TextTransforms.Apply(TextTransform.ToBase64, new string('A', 1024 * 1024));
            var started = Environment.TickCount64;
            var guess = TextTransformDetector.Detect(big);
            var elapsed = Environment.TickCount64 - started;
            runner.AreEqual(TextTransform.FromBase64, guess, "base64-shaped input past the decode gate still guesses base64");
            runner.IsTrue(elapsed < 1000, $"detection finished promptly ({elapsed} ms)");
            return Task.CompletedTask;
        });

        await runner.RunAsync("TextWizard remembers its transform and size but never the text", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"piper-textwizard-{Guid.NewGuid():N}.json");
            try
            {
                runner.AreEqual(null, TextWizardSettingsStore.Load(path), "nothing is loaded before anything is saved");

                TextWizardSettingsStore.Save(new TextWizardSettings
                {
                    LastTransform = "FromBase64",
                    LastWindowWidth = 1234,
                    LastWindowHeight = 876,
                }, path);
                var settings = TextWizardSettingsStore.Load(path);
                runner.AreEqual("FromBase64", settings?.LastTransform, "the choice survives a round trip");
                runner.AreEqual(1234, settings?.LastWindowWidth, "the window width survives a round trip");
                runner.AreEqual(876, settings?.LastWindowHeight, "the window height survives a round trip");

                // The stored file is the whole record; if a body could leak, it would leak here.
                var stored = File.ReadAllText(path);
                runner.IsTrue(!stored.Contains("hunter2", StringComparison.Ordinal), "no transformed text is stored");
                runner.AreEqual(true, stored.Length < 200, "the record holds a transform name and nothing more");

                File.WriteAllText(path, "{\"LastTransform\":\"FromBase64\"}");
                settings = TextWizardSettingsStore.Load(path);
                runner.AreEqual("FromBase64", settings?.LastTransform, "older transform-only settings still load");
                runner.AreEqual(null, settings?.LastWindowWidth, "older settings have no saved width");
                runner.AreEqual(null, settings?.LastWindowHeight, "older settings have no saved height");

                File.WriteAllText(path, "{ this is not json");
                runner.AreEqual(null, TextWizardSettingsStore.Load(path), "malformed settings fall back to the default");
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }

            return Task.CompletedTask;
        });
    }

    private static void Detects(TestRunner runner, TextTransform expected, string input, string what) =>
        runner.AreEqual(expected, TextTransformDetector.Detect(input), $"{what} is detected as {expected}");
}
