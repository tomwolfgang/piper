using System.Text;
using Piper.Core.Http;

internal static class WebFormParserTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("web forms inspector parses query and URL-encoded POST fields", () =>
        {
            var request = new HttpRequestData
            {
                Url = new Uri("https://example.test/search?q=red+fox&tag=a%2Bb"),
                RequestTarget = "/search?q=red+fox&tag=a%2Bb",
                Body = Encoding.UTF8.GetBytes("name=Tom+Wolf&empty=&encoded=%E2%9C%93"),
            };
            request.Headers.Add("Content-Type", "application/x-www-form-urlencoded; charset=utf-8");

            var fields = WebFormParser.Parse(request);
            runner.AreEqual(5, fields.Count, "query and form fields are combined");
            runner.AreEqual(new WebFormParser.Field("Query", "q", "red fox"), fields[0], "query plus is decoded");
            runner.AreEqual(new WebFormParser.Field("Query", "tag", "a+b"), fields[1], "query escapes are decoded");
            runner.AreEqual(new WebFormParser.Field("Form", "name", "Tom Wolf"), fields[2], "form plus is decoded");
            runner.AreEqual(new WebFormParser.Field("Form", "empty", string.Empty), fields[3], "empty form value survives");
            runner.AreEqual(new WebFormParser.Field("Form", "encoded", "✓"), fields[4], "UTF-8 form value is decoded");
            return Task.CompletedTask;
        });

        await runner.RunAsync("web forms inspector parses multipart text and file fields", () =>
        {
            const string boundary = "PiperBoundary";
            var fileContents = new byte[] { 0x00, 0x01, 0xFE, 0xFF };
            var body = new List<byte>();
            body.AddRange(Encoding.ASCII.GetBytes($"--{boundary}\r\n"
                + "Content-Disposition: form-data; name=title\r\n\r\n"
                + "Example\r\n"
                + $"--{boundary}\r\n"
                + "Content-Disposition: form-data; name=upload; filename=notes.bin\r\n"
                + "Content-Type: application/octet-stream\r\n\r\n"));
            body.AddRange(fileContents);
            body.AddRange(Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n"));

            var request = new HttpRequestData { Body = body.ToArray() };
            request.Headers.Add("Content-Type", $"multipart/form-data; boundary={boundary}");

            var fields = WebFormParser.Parse(request);
            runner.AreEqual(2, fields.Count, "multipart fields are extracted");
            runner.AreEqual(new WebFormParser.Field("Form", "title", "Example"), fields[0], "multipart text field");
            runner.AreEqual("[binary file: notes.bin; 4 bytes]", fields[1].Value, "multipart binary summary");
            runner.AreEqual("notes.bin", fields[1].FileName, "multipart file name");
            runner.IsTrue(fields[1].HasBinaryData, "binary field is identified");
            runner.IsTrue(fileContents.AsSpan().SequenceEqual(fields[1].BinaryData), "multipart binary data is preserved");
            return Task.CompletedTask;
        });
    }
}
