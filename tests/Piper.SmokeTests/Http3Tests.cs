using System.Text;
using Piper.Core.Http;
using Piper.Core.Http3;
using Piper.Core.Security;

// End-to-end HTTP/3: the real Http3ClientConnection against a real QUIC listener on loopback.
internal static class Http3Tests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("Alt-Svc parsing decides h3 eligibility", () =>
        {
            runner.IsTrue(AltSvcCache.AdvertisesHttp3("h3=\":443\"; ma=86400"), "plain h3");
            runner.IsTrue(AltSvcCache.AdvertisesHttp3("h3=\":443\";ma=86400,h3-29=\":443\";ma=86400"), "h3 alongside drafts");
            runner.IsTrue(!AltSvcCache.AdvertisesHttp3("h3-29=\":443\"; ma=86400"), "draft-only does not qualify");
            runner.IsTrue(!AltSvcCache.AdvertisesHttp3("h2=\"alt.example.com:443\""), "h2 alternative is not h3");

            var cache = new AltSvcCache();
            runner.IsTrue(!cache.ShouldAttempt("example.com"), "never attempted before the origin advertises it");

            cache.RecordAltSvc("example.com", "h3=\":443\"; ma=86400");
            runner.AreEqual(Http3ClientConnection.IsSupported, cache.ShouldAttempt("example.com"),
                "eligible once advertised (subject to QUIC being available at all)");

            cache.RecordFailure("example.com");
            runner.IsTrue(!cache.ShouldAttempt("example.com"), "a failed attempt suppresses retries during the cool-down");

            cache.RecordAltSvc("example.com", "clear");
            runner.IsTrue(!cache.ShouldAttempt("example.com"), "Alt-Svc: clear removes eligibility");
            return Task.CompletedTask;
        });

        if (!TestHttp3Origin.IsSupported || !Http3ClientConnection.IsSupported)
        {
            await runner.RunAsync("HTTP/3 end-to-end", () =>
            {
                runner.IsTrue(false, "QUIC unavailable on this machine - h3 end-to-end tests could not run");
                return Task.CompletedTask;
            });
            return;
        }

        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Http3-Certs"));

        await using var origin = await TestHttp3Origin.StartAsync(ca.GetCertificateFor("127.0.0.1"), EchoHandler);
        var options = new Piper.Core.Proxy.ProxyOptions { ValidateUpstreamCertificates = false };

        await runner.RunAsync("HTTP/3 GET over a real QUIC connection", async () =>
        {
            await using var connection = await Http3ClientConnection.ConnectAsync("127.0.0.1", origin.Port, options, CancellationToken.None);

            var request = new HttpRequestData { Method = "GET", Url = new Uri($"https://127.0.0.1:{origin.Port}/hello") };
            var response = await connection.SendRequestAsync(request, CancellationToken.None);

            runner.AreEqual(200, response.StatusCode, "status decoded from QPACK");
            runner.AreEqual("HTTP/3", response.HttpVersion, "tagged as h3 so the session records the real protocol");
            runner.AreEqual("GET /hello", response.BodyAsText(), "body reassembled from DATA frames");
        });

        await runner.RunAsync("HTTP/3 POST round-trips a request body", async () =>
        {
            await using var connection = await Http3ClientConnection.ConnectAsync("127.0.0.1", origin.Port, options, CancellationToken.None);

            var request = new HttpRequestData
            {
                Method = "POST",
                Url = new Uri($"https://127.0.0.1:{origin.Port}/echo"),
                Body = Encoding.UTF8.GetBytes("h3-payload"),
            };
            request.Headers.Add("Content-Type", "text/plain");

            var response = await connection.SendRequestAsync(request, CancellationToken.None);
            runner.AreEqual("POST /echo body=h3-payload", response.BodyAsText(), "origin received method, path and body");
        });

        await runner.RunAsync("HTTP/3 carries headers and status faithfully", async () =>
        {
            await using var connection = await Http3ClientConnection.ConnectAsync("127.0.0.1", origin.Port, options, CancellationToken.None);

            var request = new HttpRequestData { Method = "GET", Url = new Uri($"https://127.0.0.1:{origin.Port}/headers") };
            request.Headers.Add("X-Custom-Header", "kept-verbatim");
            request.Headers.Add("Accept", "application/json");

            var response = await connection.SendRequestAsync(request, CancellationToken.None);
            runner.AreEqual(418, response.StatusCode, "a status with no static-table entry survives");
            runner.AreEqual("bar", response.Headers["x-foo"], "response header decoded");
            runner.IsTrue(response.BodyAsText().Contains("x-custom-header=kept-verbatim"),
                "an unusual request header reached the origin");
        });

        await runner.RunAsync("HTTP/3 handles a body far larger than one frame or flow-control window", async () =>
        {
            await using var connection = await Http3ClientConnection.ConnectAsync("127.0.0.1", origin.Port, options, CancellationToken.None);

            const int size = 512 * 1024;
            var request = new HttpRequestData { Method = "GET", Url = new Uri($"https://127.0.0.1:{origin.Port}/large?size={size}") };
            var response = await connection.SendRequestAsync(request, CancellationToken.None);

            runner.AreEqual(size, response.Body.Length, "whole body arrived across many DATA frames");
            runner.IsTrue(LargePattern(size).AsSpan().SequenceEqual(response.Body), "bytes intact end to end");
        });
    }

    private static byte[] LargePattern(int size)
    {
        var body = new byte[size];
        for (var i = 0; i < size; i++) body[i] = (byte)(i * 31);
        return body;
    }

    private static Task<HttpResponseData> EchoHandler(HttpRequestData request, CancellationToken ct)
    {
        var path = request.Url!.PathAndQuery;

        if (path.StartsWith("/large", StringComparison.Ordinal))
        {
            var size = int.Parse(path[(path.IndexOf("size=", StringComparison.Ordinal) + 5)..]);
            var large = new HttpResponseData { StatusCode = 200, ReasonPhrase = "OK", Body = LargePattern(size) };
            large.Headers.Set("Content-Type", "application/octet-stream");
            return Task.FromResult(large);
        }

        if (path.StartsWith("/headers", StringComparison.Ordinal))
        {
            var seen = string.Join(",", request.Headers.Select(h => $"{h.Name}={h.Value}"));
            var teapot = HttpResponseData.Simple(418, "I'm a teapot", seen);
            teapot.Headers.Set("x-foo", "bar");
            return Task.FromResult(teapot);
        }

        var body = request.Body.Length > 0
            ? $"{request.Method} {path} body={Encoding.UTF8.GetString(request.Body)}"
            : $"{request.Method} {path}";
        return Task.FromResult(HttpResponseData.Simple(200, "OK", body));
    }
}
