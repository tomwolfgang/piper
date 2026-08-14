using Piper.Core.Http;
using Piper.Core.Sessions;

internal static class SazExporterTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("Fiddler SAZ export preserves selected exchanges", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-saz-export-{Guid.NewGuid():N}.saz");
        try
        {
            var request = new HttpRequestData
            {
                Method = "POST",
                HttpVersion = "HTTP/1.1",
                RequestTarget = "/v1/redeem",
                Url = new Uri("http://api.example.test/v1/redeem"),
                Body = [0, 1, 2, 3],
            };
            request.Headers.Add("Host", "api.example.test");
            request.Headers.Add("Content-Type", "application/octet-stream");
            request.Headers.Add("Transfer-Encoding", "chunked");

            var response = new HttpResponseData
            {
                StatusCode = 201,
                ReasonPhrase = "Created",
                Body = [4, 5, 6],
            };
            response.Headers.Add("Content-Type", "application/octet-stream");
            response.Headers.Add("Content-Length", "999");

            var written = SazExporter.Export(path,
            [
                new Session { Request = request, Response = response },
                new Session
                {
                    Request = new HttpRequestData
                    {
                        Method = "GET",
                        RequestTarget = "/pending",
                        Url = new Uri("https://api.example.test/pending"),
                    },
                },
                new Session(),
            ]);

            var result = SazImporter.Import(path);
            var importedRequest = result.Sessions[0].Request!;
            var importedResponse = result.Sessions[0].Response!;
            runner.AreEqual(2, written, "only sessions with requests are written");
            runner.AreEqual(2, result.Sessions.Count, "request and request-only sessions round-trip");
            runner.AreEqual("http://api.example.test/v1/redeem", result.Sessions[0].Url,
                "an HTTP request keeps its resolved scheme");
            runner.IsTrue(request.Body.SequenceEqual(importedRequest.Body), "request body is byte-exact");
            runner.IsTrue(response.Body.SequenceEqual(importedResponse.Body), "response body is byte-exact");
            runner.AreEqual("4", importedRequest.Headers["Content-Length"],
                "de-chunked request is exported with a matching length");
            runner.AreEqual("3", importedResponse.Headers["Content-Length"],
                "response length is updated to match its body");
            runner.AreEqual(SessionState.Failed, result.Sessions[1].State, "request-only session remains request-only");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
