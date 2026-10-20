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

    public static async Task RunSafetyAsync(TestRunner runner)
    {
        await runner.RunAsync("SAZ export: a write failing mid-archive keeps the old file and leaves no temporary file", () =>
            WithExistingTarget((folder, target, original) =>
            {
                var snapshot = SazExporter.Snapshot(Sessions(20, bodyBytes: 64 * 1024));
                var failure = Catch(() => SazExporter.Export(target, snapshot, null, CancellationToken.None,
                    file => new FailingStream(file, failAfterBytes: 4_000)));
                runner.IsTrue(failure is IOException, $"the injected write failure surfaces (got {failure?.GetType().Name ?? "none"})");
                ExpectUntouched(runner, folder, target, original);
            }));

        await runner.RunAsync("SAZ export: Cancel keeps the old file and leaves no temporary file", () =>
            WithExistingTarget((folder, target, original) =>
            {
                using var cancel = new CancellationTokenSource();
                var reports = 0;
                var progress = new InlineProgress(_ => { if (++reports == 2) cancel.Cancel(); });
                var failure = Catch(() => SazExporter.Export(target, SazExporter.Snapshot(Sessions(300, bodyBytes: 512)), progress, cancel.Token));
                runner.IsTrue(failure is OperationCanceledException, $"the export stops with a cancellation (got {failure?.GetType().Name ?? "none"})");
                runner.IsTrue(reports is >= 2 and < 100, $"it stops partway through ({reports} progress reports)");
                ExpectUntouched(runner, folder, target, original);

                var early = Catch(() => SazExporter.Export(target, SazExporter.Snapshot(Sessions(1, bodyBytes: 0)), null, new CancellationToken(canceled: true)));
                runner.IsTrue(early is OperationCanceledException, "an export cancelled before it starts writes nothing");
                ExpectUntouched(runner, folder, target, original);
            }));

        await runner.RunAsync("SAZ export: a read-only or locked target is reported and left as it was", () =>
            WithExistingTarget((folder, target, original) =>
            {
                File.SetAttributes(target, FileAttributes.ReadOnly);
                try
                {
                    var failure = Catch(() => SazExporter.Export(target, Sessions(3, bodyBytes: 10)));
                    runner.IsTrue(failure is UnauthorizedAccessException, $"a read-only target is refused (got {failure?.GetType().Name ?? "none"})");
                }
                finally
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                }
                ExpectUntouched(runner, folder, target, original);

                using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failure = Catch(() => SazExporter.Export(target, Sessions(3, bodyBytes: 10)));
                    runner.IsTrue(failure is IOException, $"a target another program holds open is refused (got {failure?.GetType().Name ?? "none"})");
                }
                ExpectUntouched(runner, folder, target, original);
            }));

        await runner.RunAsync("SAZ export: an incomplete or capped body is marked, a complete one is not", () =>
            WithExistingTarget((_, target, _) =>
            {
                var incomplete = MakeSession(1, [1, 2, 3], "application/octet-stream");
                incomplete.Response!.BodyTotalLength = 5000;
                var capped = MakeSession(2, new byte[100], "application/octet-stream");
                capped.Response!.KeepPrefix(10);
                var complete = MakeSession(3, [7, 8], "application/octet-stream");

                SazExporter.Export(target, SazExporter.Snapshot([incomplete, capped, complete]));
                var imported = SazImporter.Import(target).Sessions;

                runner.AreEqual("5000", imported[0].Response!.Headers["X-Piper-Body-Truncated"], "a partly received body carries its full length");
                runner.AreEqual("3", imported[0].Response!.Headers["Content-Length"], "its Content-Length matches the bytes written");
                runner.AreEqual("100", imported[1].Response!.Headers["X-Piper-Body-Truncated"], "a body capped to a prefix is marked");
                runner.AreEqual("10", imported[1].Response!.Headers["Content-Length"], "the capped body's length matches its prefix");
                runner.IsTrue(imported[2].Response!.Headers["X-Piper-Body-Truncated"] is null, "a complete body is not marked");
            }));

        await runner.RunAsync("SAZ export: a successful export replaces the old file and round-trips through the importer", () =>
            WithExistingTarget((folder, target, _) =>
            {
                var sessions = Sessions(40, bodyBytes: 3000);
                sessions.Add(new Session { Request = Request(99, [], contentType: null) });
                var reports = new List<int>();
                var written = SazExporter.Export(target, SazExporter.Snapshot(sessions), new InlineProgress(reports.Add));

                runner.AreEqual(41, written, "every session is written");
                runner.IsTrue(reports.Count is > 0 and <= 101 && reports[^1] == 41 && reports.SequenceEqual(reports.Order()),
                    $"progress counts up to the total ({reports.Count} reports, last {reports.LastOrDefault()})");
                runner.AreEqual(1, Directory.GetFiles(folder).Length, "no temporary file is left next to the target");

                var imported = SazImporter.Import(target).Sessions;
                runner.AreEqual(sessions.Count, imported.Count, "the importer reads every session back");
                var same = imported.Count == sessions.Count && sessions.Zip(imported).All(pair =>
                    pair.First.Url == pair.Second.Url
                    && pair.First.Request!.Headers.ToRawString() == pair.Second.Request!.Headers.ToRawString()
                    && pair.First.Request.Body.SequenceEqual(pair.Second.Request.Body)
                    && (pair.First.Response is null
                        ? pair.Second.Response is null
                        : pair.Second.Response is { } response
                          && response.StatusCode == pair.First.Response.StatusCode
                          && response.Headers.ToRawString() == pair.First.Response.Headers.ToRawString()
                          && response.Body.SequenceEqual(pair.First.Response.Body)));
                runner.IsTrue(same, "URLs, headers and bodies are identical after the round trip");
            }));

        await runner.RunAsync("SAZ export: already-compressed bodies are stored, other bodies are deflated", () =>
            WithExistingTarget((_, target, _) =>
            {
                // Zeros compress to almost nothing, so a stored entry is told apart by its size alone.
                var zeros = new byte[256 * 1024];
                var gzip = MakeSession(1, zeros, "application/json");
                gzip.Response!.Headers.Add("Content-Encoding", "gzip");
                var sessions = new[]
                {
                    gzip,
                    MakeSession(2, zeros, "image/png"),
                    MakeSession(3, zeros, "video/mp4; codecs=avc1"),
                    MakeSession(4, zeros, "text/plain; charset=utf-8"),
                };
                SazExporter.Export(target, sessions);

                using var archive = System.IO.Compression.ZipFile.OpenRead(target);
                foreach (var name in new[] { "raw/1_s.txt", "raw/2_s.txt", "raw/3_s.txt" })
                {
                    var entry = archive.GetEntry(name)!;
                    runner.IsTrue(entry.CompressedLength >= entry.Length, $"{name} is stored ({entry.CompressedLength:N0} of {entry.Length:N0} bytes)");
                }
                var text = archive.GetEntry("raw/4_s.txt")!;
                runner.IsTrue(text.CompressedLength < text.Length / 10, $"a plain text body is deflated ({text.CompressedLength:N0} of {text.Length:N0} bytes)");
                runner.IsTrue(archive.GetEntry("raw/1_c.txt")!.CompressedLength < archive.GetEntry("raw/1_c.txt")!.Length,
                    "the request beside a compressed response is still deflated");
            }));
    }

    private static Task WithExistingTarget(Action<string, string, byte[]> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"piper-saz-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var target = Path.Combine(folder, "capture.saz");
            var original = Guid.NewGuid().ToByteArray().Concat(Enumerable.Range(0, 5000).Select(i => (byte)i)).ToArray();
            File.WriteAllBytes(target, original);
            body(folder, target, original);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(folder)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void ExpectUntouched(TestRunner runner, string folder, string target, byte[] original)
    {
        runner.IsTrue(File.ReadAllBytes(target).SequenceEqual(original), "the existing file is byte-identical");
        var files = Directory.GetFiles(folder);
        runner.IsTrue(files is [var only] && only == target, $"no temporary file is left behind ({string.Join(", ", files.Select(Path.GetFileName))})");
    }

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static List<Session> Sessions(int count, int bodyBytes) =>
        [.. Enumerable.Range(1, count).Select(i =>
            MakeSession(i, [.. Enumerable.Range(0, bodyBytes).Select(b => (byte)('a' + (b + i) % 26))], "text/plain"))];

    private static Session MakeSession(int id, byte[] body, string? contentType)
    {
        var response = new HttpResponseData { StatusCode = 200, ReasonPhrase = "OK", Body = body };
        if (contentType is not null) response.Headers.Add("Content-Type", contentType);
        response.Headers.Add("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Session { Request = Request(id, [.. "name=value&other=value"u8], "application/x-www-form-urlencoded"), Response = response };
    }

    private static HttpRequestData Request(int id, byte[] body, string? contentType)
    {
        var request = new HttpRequestData
        {
            Method = body.Length == 0 ? "GET" : "POST",
            HttpVersion = "HTTP/1.1",
            RequestTarget = $"/item/{id}",
            Url = new Uri($"https://api.example.test/item/{id}"),
            Body = body,
        };
        request.Headers.Add("Host", "api.example.test");
        if (contentType is not null) request.Headers.Add("Content-Type", contentType);
        if (body.Length > 0) request.Headers.Add("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>, so a test sees each report in order.</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    /// <summary>Passes writes through until a byte budget is spent, then throws as a full disk would.</summary>
    private sealed class FailingStream(Stream inner, long failAfterBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written > failAfterBytes) throw new IOException("There is not enough space on the disk (injected).");
            inner.Write(buffer, offset, count);
        }
    }
}
