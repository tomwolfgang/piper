using System.IO.Compression;
using System.Text;
using Piper.Core.Sessions;

internal static class SazImporterTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("Fiddler SAZ import preserves exchanges", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-saz-{Guid.NewGuid():N}.saz");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "raw/1_c.txt", "POST https://api.example.test/v1/redeem HTTP/1.1\r\nHost: api.example.test\r\nContent-Type: application/json\r\n\r\n{\"code\":\"ABC\"}");
                Write(archive, "raw/1_s.txt", "HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\n\r\n{\"message\":\"already redeemed\"}");
                Write(archive, "raw/2_c.txt", "GET https://api.example.test/v1/items HTTP/1.1\r\nHost: api.example.test\r\n\r\n");
                Write(archive, "raw/2_s.txt", "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n[]");
            }

            var result = SazImporter.Import(path);
            runner.AreEqual(2, result.Sessions.Count, "all raw request/response pairs imported");
            runner.AreEqual(0, result.Warnings.Count, "valid archive has no warnings");
            runner.AreEqual("POST", result.Sessions[0].Method, "request method imported");
            runner.AreEqual("https://api.example.test/v1/redeem", result.Sessions[0].Url, "absolute URL imported");
            runner.AreEqual(400, result.Sessions[0].StatusCode, "response status imported");
            runner.AreEqual("{\"code\":\"ABC\"}", result.Sessions[0].Request!.BodyAsText(), "request body imported");
            runner.AreEqual("{\"message\":\"already redeemed\"}", result.Sessions[0].Response!.BodyAsText(), "response body imported");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });

    /// <summary>Regression test: a "request-only" capture (present but empty response entries,
    /// as produced by some Fiddler request-archive exports) must still import the requests.</summary>
    public static Task RunEmptyResponseAsync(TestRunner runner) => runner.RunAsync(
        "Fiddler SAZ import keeps the request when its response entry is empty", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-saz-{Guid.NewGuid():N}.saz");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "raw/1_c.txt", "GET https://api.example.test/v1/items HTTP/1.1\r\nHost: api.example.test\r\n\r\n");
                archive.CreateEntry("raw/1_s.txt"); // present but empty, e.g. no response ever captured
            }

            var result = SazImporter.Import(path);
            runner.AreEqual(1, result.Sessions.Count, "request imported despite the empty response entry");
            runner.AreEqual(1, result.Warnings.Count, "empty response entry is reported as a warning");
            runner.AreEqual("GET", result.Sessions[0].Method, "request method imported");
            runner.IsTrue(result.Sessions[0].Response is null, "no response attached");
            runner.AreEqual(SessionState.Failed, result.Sessions[0].State, "session marked as response-less");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });

    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }
}
