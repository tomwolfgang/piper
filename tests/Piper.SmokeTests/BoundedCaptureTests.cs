using System.Net;
using System.Security.Cryptography;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Keeping only part of a large body, and saying so everywhere a size or a body is reported.
internal static class BoundedCaptureTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a body past the capture limit is relayed whole but kept in part", async () =>
        {
            var payload = RandomNumberGenerator.GetBytes(256 * 1024);
            var expected = Convert.ToHexString(SHA256.HashData(payload));
            const int Keep = 16 * 1024;

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {payload.Length}\r\n\r\n", ct);
                await stream.WriteAsync(payload, ct);
                return false;
            });

            using var harness = new ProxyHarness(o => o.MaxCapturedBodyBytes = Keep);
            using var client = harness.CreateClient();

            var got = await client.GetByteArrayAsync($"http://127.0.0.1:{origin.Port}/big.zip");

            // The client is the part that must not be affected by a capture decision.
            runner.AreEqual(payload.Length, got.Length, "the client still receives every byte");
            runner.AreEqual(expected, Convert.ToHexString(SHA256.HashData(got)), "unaltered");

            var session = harness.Store.Snapshot().Last(s => s.Url.Contains("big.zip", StringComparison.Ordinal));

            // The proxy writes the last bytes to the client first and only then records the captured
            // body on the session and marks it complete, so a client that has everything may be here
            // before the proxy has finished. Complete is set after the body is, hence the wait.
            runner.IsTrue(await Poll.UntilAsync(() => session.State == SessionState.Complete), "the session completes");
            runner.AreEqual(Keep, session.Response!.Body.Length, "only the limit is retained");
            runner.AreEqual((long)payload.Length, session.Response.BodyTotalLength,
                "while the size reported is what crossed the wire");
            runner.AreEqual((long)payload.Length, session.ResponseSize, "which is what the grid shows");
            runner.IsTrue(!session.Response.IsBodyComplete, "and the capture is marked incomplete");
            runner.AreEqual(Convert.ToHexString(SHA256.HashData(payload[..Keep])),
                Convert.ToHexString(SHA256.HashData(session.Response.Body)),
                "the retained part is the start of the body, byte-exact");
        });

        await runner.RunAsync("a body within the limit is captured whole and not flagged", async () =>
        {
            // The negative control: without it, a flag stuck on would look like a passing test.
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nsmall", ct);
                return false;
            });

            using var harness = new ProxyHarness(o => o.MaxCapturedBodyBytes = 16 * 1024);
            using var client = harness.CreateClient();

            runner.AreEqual("small", await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/small.txt"),
                "the body arrives");

            var session = harness.Store.Snapshot().Last(s => s.Url.Contains("small.txt", StringComparison.Ordinal));
            runner.IsTrue(await Poll.UntilAsync(() => session.State == SessionState.Complete), "the session completes");
            runner.IsTrue(session.Response!.IsBodyComplete, "a small body is complete");
            runner.AreEqual(5L, session.ResponseSize, "and reports its own size");
        });

        await runner.RunAsync("the store bounds how many body bytes it keeps", () =>
        {
            // Asserted over bytes computed from the sessions themselves. Process memory would be a
            // reading of the garbage collector's mood, not of what the store is holding on to.
            var store = new SessionStore { RetainedBodyBudgetBytes = 200_000 };

            for (var i = 0; i < 40; i++)
            {
                var response = new HttpResponseData { Body = new byte[20_000] };
                response.BodyTotalLength = 20_000;
                store.Add(new Session
                {
                    Request = new HttpRequestData { RequestTarget = $"/file-{i}" },
                    Response = response,
                    State = SessionState.Complete,
                    Completed = DateTimeOffset.Now,
                });
            }

            var all = store.Snapshot();
            runner.AreEqual(40, all.Length, "every session is kept -- only bodies are released");
            runner.IsTrue(all.Sum(s => s.Response!.Body.LongLength) <= 200_000,
                $"retained bytes stay within the budget (held {all.Sum(s => s.Response!.Body.LongLength)})");

            runner.AreEqual(0, all[0].Response!.Body.Length, "the oldest body is the one released");
            runner.IsTrue(all[^1].Response!.Body.Length > 0, "while the newest is still there to look at");

            // The important part: a released body must not start reading as an empty one.
            runner.AreEqual(20_000L, all[0].Response!.BodyTotalLength, "a released body keeps its true size");
            runner.AreEqual(20_000L, all[0].ResponseSize, "so the grid still reports what was transferred");
            runner.AreEqual("/file-0", all[0].Request!.RequestTarget, "and the session itself survives");

            return Task.CompletedTask;
        });

        await runner.RunAsync("a body read whole is kept only as far as a relayed one", () =>
        {
            // HTTP/3 still reads a body whole. What the capture keeps of it must follow the same
            // limit as a relayed body, or one large download is retained in full and the budget
            // releases every other session's body to make room for it.
            var response = new HttpResponseData { Body = new byte[100_000] };
            response.KeepPrefix(4_096);

            runner.AreEqual(4_096, response.Body.Length, "only the limit is kept");
            runner.AreEqual(100_000L, response.BodyTotalLength, "the true size is still reported");
            runner.IsTrue(!response.IsBodyComplete, "and it is flagged as partial");

            var small = new HttpResponseData { Body = new byte[100] };
            small.KeepPrefix(4_096);
            runner.IsTrue(small.Body.Length == 100 && small.IsBodyComplete, "a body under the limit is untouched");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a compressed body kept only in part still decodes without throwing", () =>
        {
            // A capture limit cuts a gzip stream mid-member, which a whole-or-nothing capture never
            // produced. The inspector decodes on the UI thread, so this has to degrade, not throw.
            using var compressed = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("partial capture ", 20_000))));

            var response = new HttpResponseData { Body = compressed.ToArray() };
            response.Headers.Set("Content-Encoding", "gzip");
            response.Headers.Set("Content-Type", "text/plain");
            response.KeepPrefix(response.Body.Length / 2);

            string outcome;
            try
            {
                var decoded = response.DecodedBody;
                _ = response.BodyAsText(decoded);
                outcome = "decoded";
            }
            catch (Exception ex) { outcome = $"threw {ex.GetType().Name}"; }

            runner.AreEqual("decoded", outcome, "the truncated stream decodes as far as it goes, or falls back to the raw bytes");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the budget counts a body attached after the session was admitted", () =>
        {
            // The order the proxy actually uses: the session is added while the request is still in
            // flight, and the response body only exists once it has been relayed.
            var store = new SessionStore { RetainedBodyBudgetBytes = 200_000 };
            var sessions = new List<Session>();

            for (var i = 0; i < 40; i++)
            {
                var session = new Session { Request = new HttpRequestData { RequestTarget = $"/late-{i}" } };
                store.Add(session);
                sessions.Add(session);

                session.Response = new HttpResponseData { Body = new byte[20_000], BodyTotalLength = 20_000 };
                session.State = SessionState.Complete;
                session.Completed = DateTimeOffset.Now;
                store.NotifyUpdated(session);
            }

            runner.IsTrue(sessions.Sum(s => s.Response!.Body.LongLength) <= 200_000,
                $"bodies attached later stay within the budget (held {sessions.Sum(s => s.Response!.Body.LongLength)})");
            runner.AreEqual(0, sessions[0].Response!.Body.Length, "the oldest body is the one released");
            return Task.CompletedTask;
        });

        await runner.RunAsync("sessions leaving the store stop counting against the budget", () =>
        {
            // Without this the total only ever grows, and bodies well within budget get released.
            foreach (var (name, evict) in new (string, Action<SessionStore>)[]
                     {
                         ("removed", store => store.RemoveAll(s => s.Url.Contains("/old-", StringComparison.Ordinal))),
                         ("trimmed by capacity", store => store.Capacity = 5),
                     })
            {
                var store = new SessionStore { RetainedBodyBudgetBytes = 200_000 };
                void AddBody(string path) => store.Add(new Session
                {
                    Request = new HttpRequestData { RequestTarget = path },
                    Response = new HttpResponseData { Body = new byte[20_000], BodyTotalLength = 20_000 },
                    State = SessionState.Complete,
                    Completed = DateTimeOffset.Now,
                });

                for (var i = 0; i < 10; i++) AddBody($"/old-{i}");   // exactly the budget
                evict(store);
                for (var i = 0; i < 5; i++) AddBody($"/new-{i}");

                var kept = store.Snapshot().Where(s => s.Url.Contains("/new-", StringComparison.Ordinal)).ToArray();
                runner.IsTrue(kept.Length == 5 && kept.All(s => s.Response!.Body.Length == 20_000),
                    $"after sessions are {name}, new bodies within budget are kept");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("an exported archive does not pass a fragment off as a whole body", () =>
        {
            var partial = new HttpResponseData { Body = Encoding.Latin1.GetBytes("first-half") };
            partial.Headers.Set("Content-Length", "99999");
            partial.BodyTotalLength = 99_999;

            var session = new Session
            {
                Request = new HttpRequestData { Url = new Uri("http://example.test/big"), RequestTarget = "/big" },
                Response = partial,
                State = SessionState.Complete,
                Completed = DateTimeOffset.Now,
            };

            var path = Path.Combine(Path.GetTempPath(), $"Piper-SmokeTest-Truncated-{Guid.NewGuid():N}.saz");
            try
            {
                SazExporter.Export(path, [session]);
                var reimported = SazImporter.Import(path);

                runner.AreEqual(1, reimported.Sessions.Count, "the archive round-trips");
                var exported = reimported.Sessions[0].Response!;

                runner.AreEqual("10", exported.Headers["Content-Length"],
                    "the archive describes the bytes it actually contains");
                runner.AreEqual("99999", exported.Headers["X-Piper-Body-Truncated"],
                    "and records the length the body really had");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }

            return Task.CompletedTask;
        });
    }

    private sealed class ProxyHarness : IDisposable
    {
        private readonly CertificateAuthority _ca;
        private readonly ProxyServer _proxy;

        public ProxyHarness(Action<ProxyOptions>? configure = null)
        {
            _ca = CertificateAuthority.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Capture-Certs"));
            Store = new SessionStore();
            var options = new ProxyOptions { Port = 0 };
            configure?.Invoke(options);
            _proxy = new ProxyServer(options, _ca, Store);
            _proxy.Start();
            Port = _proxy.Endpoint!.Port;
        }

        public int Port { get; }

        public SessionStore Store { get; }

        public HttpClient CreateClient() => new(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Port}", BypassOnLocal: false),
            UseProxy = true,
        })
        { Timeout = TimeSpan.FromSeconds(30) };

        public void Dispose()
        {
            _proxy.StopAsync().GetAwaiter().GetResult();
            _ca.Dispose();
        }
    }
}
