using Piper.App.Controls;

// The inspector serves a captured video from memory to the browser engine's media stack, which asks for it in
// byte ranges. The Range header and the Content-Type are decided here, away from WebView2, and both come from an
// attacker's file or headers, so these tests include malformed, inverted, overflowing and several-range input.
internal static class VideoRangeTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a request with no Range, or another unit, gets the whole body", () =>
        {
            foreach (var header in new[] { null, "", "   ", "items=0-9", "pages=1" })
                Expect(runner, header, 1000, new RangeAnswer(200, 0, 1000, null), $"Range {header ?? "(none)"}");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a single range is answered with 206 and its Content-Range", () =>
        {
            Expect(runner, "bytes=0-99", 1000, new RangeAnswer(206, 0, 100, "bytes 0-99/1000"), "first bytes");
            Expect(runner, "bytes=500-599", 1000, new RangeAnswer(206, 500, 100, "bytes 500-599/1000"), "middle");
            Expect(runner, "bytes=900-999", 1000, new RangeAnswer(206, 900, 100, "bytes 900-999/1000"), "last bytes");
            Expect(runner, "bytes=999-999", 1000, new RangeAnswer(206, 999, 1, "bytes 999-999/1000"), "the final byte");
            Expect(runner, "bytes=0-0", 1, new RangeAnswer(206, 0, 1, "bytes 0-0/1"), "a one-byte body");
            Expect(runner, "bytes=900-", 1000, new RangeAnswer(206, 900, 100, "bytes 900-999/1000"), "open-ended");
            Expect(runner, "bytes=0-", 1000, new RangeAnswer(206, 0, 1000, "bytes 0-999/1000"), "open-ended from the start (what a media element sends first)");
            Expect(runner, "bytes=-100", 1000, new RangeAnswer(206, 900, 100, "bytes 900-999/1000"), "suffix");
            Expect(runner, "bytes=-1", 1000, new RangeAnswer(206, 999, 1, "bytes 999-999/1000"), "one-byte suffix");
            Expect(runner, "BYTES=0-9", 1000, new RangeAnswer(206, 0, 10, "bytes 0-9/1000"), "the unit is case-insensitive");
            Expect(runner, " bytes=0-9 ", 1000, new RangeAnswer(206, 0, 10, "bytes 0-9/1000"), "surrounding blanks");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a range running past the end is cut to it, and huge numbers do not overflow", () =>
        {
            Expect(runner, "bytes=990-5000", 1000, new RangeAnswer(206, 990, 10, "bytes 990-999/1000"), "last byte past the end");
            Expect(runner, "bytes=-5000", 1000, new RangeAnswer(206, 0, 1000, "bytes 0-999/1000"), "suffix longer than the body is the whole body");
            Expect(runner, "bytes=0-99999999999999999999", 1000, new RangeAnswer(206, 0, 1000, "bytes 0-999/1000"), "a last byte beyond long");
            Expect(runner, "bytes=-99999999999999999999", 1000, new RangeAnswer(206, 0, 1000, "bytes 0-999/1000"), "a suffix beyond long");
            Expect(runner, "bytes=0-9223372036854775807", 1000, new RangeAnswer(206, 0, 1000, "bytes 0-999/1000"), "long.MaxValue as the last byte");
            Expect(runner, "bytes=0-", long.MaxValue, new RangeAnswer(206, 0, long.MaxValue, $"bytes 0-{long.MaxValue - 1}/{long.MaxValue}"), "the largest possible body");
            Expect(runner, "bytes=9223372036854775807-", long.MaxValue, new RangeAnswer(416, 0, 0, $"bytes */{long.MaxValue}"), "a first byte equal to the largest length");
            return Task.CompletedTask;
        });

        await runner.RunAsync("an unsatisfiable or malformed range is 416 with bytes */length", () =>
        {
            var refused = new RangeAnswer(416, 0, 0, "bytes */1000");
            foreach (var header in new[]
            {
                "bytes=1000-", "bytes=1000-1005", "bytes=2000-3000", "bytes=99999999999999999999-", "bytes=99999999999999999999-99999999999999999999",
                "bytes=-0", "bytes=5-4", "bytes=900-100",
                "bytes=", "bytes=-", "bytes=5", "bytes=abc", "bytes=1-x", "bytes=x-1", "bytes=0x10-20", "bytes=+1-5", "bytes=-+5", "bytes=1-5-9", "bytes=1 2-5", "bytes=1.5-3",
                "bytes=--5", "bytes=٣-٥",
            })
                Expect(runner, header, 1000, refused, $"Range {header}");
            return Task.CompletedTask;
        });

        await runner.RunAsync("several ranges are not supported and get the whole body", () =>
        {
            var whole = new RangeAnswer(200, 0, 1000, null);
            foreach (var header in new[] { "bytes=0-9,20-29", "bytes=0-9, 2000-3000", "bytes=-5,5-", "bytes=0-9,", "bytes=,", "bytes=0-9,nonsense" })
                Expect(runner, header, 1000, whole, $"Range {header}");
            return Task.CompletedTask;
        });

        await runner.RunAsync("an empty body has no satisfiable range", () =>
        {
            Expect(runner, null, 0, new RangeAnswer(200, 0, 0, null), "no Range is an empty 200");
            foreach (var header in new[] { "bytes=0-", "bytes=0-0", "bytes=-5", "bytes=-0", "bytes=1-" })
                Expect(runner, header, 0, new RangeAnswer(416, 0, 0, "bytes */0"), $"Range {header}");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the served type is a fixed string chosen from the declared type or the first bytes", () =>
        {
            byte[] None = [];
            runner.AreEqual("video/mp4", VideoRange.MimeFor("video/mp4", None), "declared mp4");
            runner.AreEqual("video/webm", VideoRange.MimeFor("Video/WebM; codecs=\"vp8\"", None), "declared webm, any case, with parameters");
            runner.AreEqual("video/ogg", VideoRange.MimeFor("video/ogg", None), "declared ogg");
            runner.AreEqual("video/quicktime", VideoRange.MimeFor("video/quicktime", None), "declared quicktime");
            runner.AreEqual("video/x-msvideo", VideoRange.MimeFor("video/x-msvideo", None), "declared avi");
            runner.AreEqual("video/mp4", VideoRange.MimeFor(null, [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m']), "an ftyp box");
            runner.AreEqual("video/ogg", VideoRange.MimeFor("text/html", "OggS\0\0\0\0"u8.ToArray()), "OggS wins over a wrong declared type");
            runner.AreEqual("video/webm", VideoRange.MimeFor("", [0x1A, 0x45, 0xDF, 0xA3, 0x01]), "an EBML header");
            runner.AreEqual("video/x-msvideo", VideoRange.MimeFor(null, "RIFF\0\0\0\0AVI LIST"u8.ToArray()), "a RIFF AVI header");
            runner.AreEqual("application/octet-stream", VideoRange.MimeFor("video/x-matroska", "not a video at all"u8.ToArray()), "an unknown body");
            runner.AreEqual("application/octet-stream", VideoRange.MimeFor("RIFF", "RIFF\0\0\0\0WAVEfmt "u8.ToArray()), "a RIFF that is not AVI");
            foreach (var hostile in new[] { "video/mp4\r\nSet-Cookie: x=1", "video/mp4\nX: y", "\r\n\r\n<script>", new string('v', 10_000) })
                runner.IsTrue(VideoRange.MimeFor(hostile, None) == "application/octet-stream", "a hostile Content-Type is never copied into the response");
            return Task.CompletedTask;
        });
    }

    private static void Expect(TestRunner runner, string? header, long length, RangeAnswer expected, string what) =>
        runner.AreEqual(expected, VideoRange.Resolve(header, length), what);
}
