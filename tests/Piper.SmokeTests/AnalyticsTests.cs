using System.Net;
using System.Net.Sockets;
using System.Text;
using Piper.Core.Telemetry;

/// <summary>
/// Covers the analytics trust boundary. The sanitiser is the reason captured traffic cannot leave, so
/// it is exercised against the shapes real leaks would take: URLs, headers, bodies, and paths.
/// </summary>
internal static class AnalyticsTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("analytics: the schema drops unknown events and keys", () =>
        {
            var unknown = AnalyticsSchema.Create("piper_traffic_captured", null, DateTimeOffset.UtcNow, "run", 1);
            runner.IsTrue(unknown is null, "an event name outside the allowlist is dropped");

            var known = AnalyticsSchema.Create(
                AnalyticsEvents.FeatureUsed,
                [(AnalyticsProperties.Feature, "composer"), ("url", "https://bank.example/login")],
                DateTimeOffset.UtcNow,
                "run",
                1);
            runner.IsTrue(known is not null, "an allowlisted event survives");
            runner.AreEqual(1, known!.Properties.Count, "the unknown property key is dropped");
            runner.AreEqual("composer", known.Properties[AnalyticsProperties.Feature], "the known property is kept");
            runner.IsTrue(!known.Properties.ContainsKey("url"), "a smuggled url key never reaches the event");

            // The run identifier travels outside the property bag, so it has to be sanitised by the
            // boundary itself rather than by whoever happens to call it.
            var hostileRun = AnalyticsSchema.Create(
                AnalyticsEvents.AppStarted, null, DateTimeOffset.UtcNow, "https://bank.example/x?a=b", 1);
            runner.AreEqual(AnalyticsSchema.InvalidValue, hostileRun!.RunId, "a hostile run id is replaced");
            return Task.CompletedTask;
        });

        await runner.RunAsync("analytics: the sanitiser rejects everything that could carry user data", () =>
        {
            // Each of these is a shape a real leak would take if a future call site got careless.
            var leaks = new[]
            {
                "https://bank.example/account?token=abc",
                "Authorization: Bearer abc123",
                "user@example.com",
                "C:\\Users\\someone\\Desktop\\capture.saz",
                "line one\nline two",
                "sess ion",
                "hello\u00e9world",
                "{\"password\":\"hunter2\"}",
                new string('a', AnalyticsSchema.MaxValueLength + 1),
                new string('b', 10_000),
                string.Empty,
                null,
            };

            foreach (var leak in leaks)
            {
                runner.AreEqual(
                    AnalyticsSchema.InvalidValue,
                    AnalyticsSchema.SanitiseValue(leak),
                    $"rejected: {Describe(leak)}");
            }

            foreach (var acceptable in new[] { "composer", "saz", "1m-5m", "IOException", "a.b_c-d", "gt999" })
            {
                runner.AreEqual(acceptable, AnalyticsSchema.SanitiseValue(acceptable), $"kept: {acceptable}");
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("analytics: a property flood is bounded", () =>
        {
            var many = Enumerable.Range(0, 50)
                .Select(_ => (AnalyticsProperties.Feature, "x"))
                .ToArray();
            var recorded = AnalyticsSchema.Create(AnalyticsEvents.FeatureUsed, many, DateTimeOffset.UtcNow, "run", 1);
            runner.IsTrue(
                recorded is not null && recorded.Properties.Count <= AnalyticsSchema.MaxProperties,
                "property count stays within the cap");
            return Task.CompletedTask;
        });

        await runner.RunAsync("analytics: errors report a type and frames but never a message", () =>
        {
            Exception captured;
            try
            {
                throw new InvalidOperationException("connecting to https://secret.internal/api failed for bob");
            }
            catch (InvalidOperationException ex)
            {
                captured = ex;
            }

            var frames = Analytics.DescribeFrames(captured);
            runner.AreEqual(frames, AnalyticsSchema.SanitiseValue(frames), "frames survive the sanitiser unchanged");
            runner.IsTrue(!frames.Contains("secret", StringComparison.OrdinalIgnoreCase), "frames carry no message text");
            runner.IsTrue(!frames.Contains('\\') && !frames.Contains('/'), "frames carry no file path");

            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            Analytics.Initialize(temp.CreateClient());
            try
            {
                Analytics.TrackError("saz.import", captured, fatal: true);
                Analytics.FlushToDisk();
            }
            finally
            {
                Analytics.Shutdown();
            }

            var line = temp.ReadSpool().Single();
            runner.IsTrue(!line.Contains("secret.internal", StringComparison.Ordinal), "the spooled error omits the url");
            runner.IsTrue(!line.Contains("bob", StringComparison.Ordinal), "the spooled error omits the user name");
            runner.IsTrue(line.Contains("InvalidOperationException", StringComparison.Ordinal), "the type is recorded");
            runner.IsTrue(line.Contains("\"fatal\":\"true\"", StringComparison.Ordinal), "fatal is recorded");
            return Task.CompletedTask;
        });

        await runner.RunAsync("analytics: settings round-trip and fall back to defaults", () =>
        {
            var directory = Directory.CreateTempSubdirectory("piper-analytics-settings");
            try
            {
                var path = Path.Combine(directory.FullName, "analytics.json");
                runner.IsTrue(AnalyticsSettingsStore.Load(path) is null, "a missing file loads as null");

                AnalyticsSettingsStore.Save(
                    new AnalyticsSettings { Enabled = false, InstallId = "abc", NoticeShownVersion = "0.4.0" }, path);
                var loaded = AnalyticsSettingsStore.Load(path);
                runner.IsTrue(loaded is not null, "the saved file loads");
                runner.AreEqual(false, loaded!.Enabled, "Enabled round-trips");
                runner.AreEqual("abc", loaded.InstallId, "InstallId round-trips");
                runner.AreEqual("0.4.0", loaded.NoticeShownVersion, "NoticeShownVersion round-trips");

                File.WriteAllText(path, "{ this is not json");
                runner.IsTrue(AnalyticsSettingsStore.Load(path) is null, "a corrupt file loads as null");

                runner.AreEqual(false, new AnalyticsSettings().Enabled, "collection is opt-in, not opt-out");
                runner.IsTrue(new AnalyticsSettings().NoticeShownVersion is null, "and nobody has been asked yet");
            }
            finally
            {
                directory.Delete(recursive: true);
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("analytics: a fresh install collects nothing until asked", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();

            // Nothing has been configured and nobody has been asked - the state every new install
            // starts in. Collection is opt-in, so this must be completely inert.
            using var client = temp.CreateClient(server.Endpoint);
            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CaptureStarted, (AnalyticsProperties.Result, "ok"));
            await client.FlushAsync();

            runner.AreEqual(false, temp.Settings.Enabled, "the default is off");
            runner.IsTrue(!File.Exists(temp.SpoolPath), "nothing is written to disk");
            runner.AreEqual(0, server.RequestCount, "nothing is sent");
            runner.IsTrue(temp.Settings.InstallId is null, "no identifier is created");

            // Declining is recorded, and must not quietly start collection.
            client.SetEnabled(false);
            client.RecordNoticeShown("0.4.0");
            client.Track(AnalyticsEvents.FirstSessionCaptured, (AnalyticsProperties.Result, "ok"));
            await client.FlushAsync();

            runner.IsTrue(!File.Exists(temp.SpoolPath), "declining keeps it inert");
            runner.AreEqual(0, server.RequestCount, "and still sends nothing");
        });

        await runner.RunAsync("analytics: opting out stops collection at the source", async () =>
        {
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = false;
            temp.Settings.NoticeShownVersion = "0.4.0";

            using var client = temp.CreateClient();
            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CaptureStarted, (AnalyticsProperties.Result, "AllProcesses"));
            await client.FlushAsync();

            runner.IsTrue(!File.Exists(temp.SpoolPath), "nothing is written to disk while opted out");
        });

        await runner.RunAsync("analytics: nothing is uploaded before the notice is shown", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            // NoticeShownVersion deliberately left null: they asked for this but have not been asked yet.
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            await client.FlushAsync();

            runner.AreEqual(1, temp.ReadSpool().Length, "the event is spooled");
            runner.AreEqual(0, server.RequestCount, "but no request is made");
            runner.IsTrue(temp.Settings.InstallId is null, "and no install id is minted");

            // Once the notice has been recorded, the same spool delivers.
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            await client.FlushAsync();

            runner.AreEqual(1, server.RequestCount, "after the notice, the spool is delivered");
            runner.AreEqual(0, temp.ReadSpool().Length, "and the delivered events are cleared");
            runner.IsTrue(!string.IsNullOrEmpty(temp.Settings.InstallId), "an install id is minted on first delivery");
            runner.IsTrue(
                server.LastRequest.Contains("Name=" + AnalyticsEvents.AppStarted, StringComparison.Ordinal),
                "the request names the event");
            runner.IsTrue(
                server.LastRequest.Contains("/analytics/Counter", StringComparison.Ordinal)
                    && server.LastRequest.Contains("MUID=", StringComparison.Ordinal),
                "and matches the Counter contract");
            runner.IsTrue(
                server.LastRequest.Contains("\"app_ver\"", StringComparison.Ordinal)
                    && server.LastRequest.Contains("\"app_type\":\"piper\"", StringComparison.Ordinal),
                "and carries the base keys the shared dashboards expect");
        });

        await runner.RunAsync("analytics: every event carries its position in the run", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            client.Track("piper_traffic_captured");
            client.Track(AnalyticsEvents.CaptureStarted, (AnalyticsProperties.Result, "port_in_use"));
            client.Track(AnalyticsEvents.CertTrusted,
                (AnalyticsProperties.Source, "startup"), (AnalyticsProperties.Result, "declined"));
            await client.FlushAsync();

            runner.AreEqual(3, server.RequestCount, "the refused name is not sent");
            runner.IsTrue(
                server.Requests[1].Contains("\"result\":\"port_in_use\"", StringComparison.Ordinal),
                "a capture outcome reaches the wire");
            runner.IsTrue(
                server.Requests[2].Contains("\"result\":\"declined\"", StringComparison.Ordinal)
                    && server.Requests[2].Contains("\"source\":\"startup\"", StringComparison.Ordinal)
                    && server.Requests[2].Contains("\"seq\":\"3\"", StringComparison.Ordinal),
                "and so does a certificate outcome, which the allowlist must not drop");
            runner.IsTrue(server.Requests[0].Contains("\"seq\":\"1\"", StringComparison.Ordinal), "the first event is 1");
            runner.IsTrue(
                server.Requests[1].Contains("\"seq\":\"2\"", StringComparison.Ordinal),
                "and a refused name takes no number, so a gap means a lost event");

            // The spool is user-editable: a negative number is clamped, and a line written before
            // the field existed still delivers.
            Directory.CreateDirectory(Path.GetDirectoryName(temp.SpoolPath)!);
            File.WriteAllLines(temp.SpoolPath,
            [
                $"{{\"name\":\"{AnalyticsEvents.AppStarted}\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r1\",\"seq\":-7}}",
                $"{{\"name\":\"{AnalyticsEvents.AppStarted}\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r2\"}}",
            ]);
            using var nextRun = temp.CreateClient(server.Endpoint);
            await nextRun.FlushAsync();

            runner.AreEqual(5, server.RequestCount, "both hand-written lines deliver");
            runner.IsTrue(
                server.Requests[3].Contains("\"seq\":\"0\"", StringComparison.Ordinal)
                    && server.Requests[4].Contains("\"seq\":\"0\"", StringComparison.Ordinal),
                "a negative or missing sequence reads as 0");
        });

        await runner.RunAsync("analytics: a failed upload keeps the spool", async () =>
        {
            using var server = new LoopbackCollector(statusCode: 500);
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            await client.FlushAsync();

            runner.AreEqual(1, server.RequestCount, "delivery was attempted");

            // Asserted as behaviour rather than by looking for a particular file: what matters is
            // that a batch the collector refused is still there to go out again. A fresh client on
            // the same directory is also the real recovery path for a run that died mid-upload -
            // and it sidesteps the backoff the failure just armed on this one.
            server.StatusCode = 202;
            using var nextRun = temp.CreateClient(server.Endpoint);
            await nextRun.FlushAsync();

            runner.AreEqual(2, server.RequestCount, "the rejected batch is retried on the next run");
            runner.IsTrue(
                server.LastRequest.Contains(AnalyticsEvents.AppStarted, StringComparison.Ordinal),
                "and it still carries the original event");
        });

        await runner.RunAsync("analytics: the spool is capped and drops the oldest", async () =>
        {
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            Directory.CreateDirectory(Path.GetDirectoryName(temp.SpoolPath)!);
            var filler = new StringBuilder();
            for (var i = 0; i < 40_000; i++)
            {
                filler.Append("{\"name\":\"piper_usage_app_started\",\"seq\":").Append(i).Append("}\n");
            }

            File.WriteAllText(temp.SpoolPath, filler.ToString());
            var before = new FileInfo(temp.SpoolPath).Length;
            runner.IsTrue(before > 1024 * 1024, "the spool starts over the cap");

            using var client = temp.CreateClient();
            client.Track(AnalyticsEvents.FirstSessionCaptured, (AnalyticsProperties.Result, "ok"));
            client.FlushToDisk();

            var lines = temp.ReadSpool();
            runner.IsTrue(new FileInfo(temp.SpoolPath).Length < before, "the spool was trimmed");
            runner.IsTrue(
                !lines[0].Contains("\"seq\":0}", StringComparison.Ordinal),
                "the oldest events are the ones dropped");
            runner.IsTrue(
                lines[^1].Contains(AnalyticsEvents.FirstSessionCaptured, StringComparison.Ordinal),
                "the newest event is kept");
            await Task.CompletedTask;
        });

        await runner.RunAsync("analytics: spooled text is re-validated before it is sent", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            // The spool lives in the user's profile and the UI invites them to open it, so what is
            // read back is untrusted. A hand-edited value and a half-written line (which the crash
            // handler can leave behind, since appending is not atomic) must not reach the wire.
            Directory.CreateDirectory(Path.GetDirectoryName(temp.SpoolPath)!);
            File.WriteAllText(temp.SpoolPath,
                "{\"name\":\"piper_usage_feature_used\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r\","
                + "\"props\":{\"feature\":\"https://bank.example/statement\"}}\n"
                + "{\"name\":\"piper_traffic_captured\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r\",\"props\":{}}\n"
                + "{\"name\":\"piper_usage_app_started\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r\",\"pro\n");

            using var client = temp.CreateClient(server.Endpoint);
            await client.FlushAsync();

            runner.AreEqual(1, server.RequestCount, "a truncated line does not stop the batch going out");
            runner.IsTrue(
                !server.LastRequest.Contains("bank.example", StringComparison.Ordinal),
                "a url hand-written into the spool is stripped on the way out");
            runner.IsTrue(
                server.LastRequest.Contains(AnalyticsSchema.InvalidValue, StringComparison.Ordinal),
                "and is replaced rather than passed through");
            runner.IsTrue(
                !server.LastRequest.Contains("piper_traffic_captured", StringComparison.Ordinal),
                "an event name outside the allowlist is dropped on the way out too");
        });

        await runner.RunAsync("analytics: events arriving mid-delivery are not lost", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            client.FlushToDisk();

            // Stands in for the crash handler writing while a batch is in the air: the delivered
            // batch must be discarded by identity, never by counting lines off a file that moved.
            server.BeforeRespond = () =>
            {
                client.Track(AnalyticsEvents.CaptureStarted, (AnalyticsProperties.Result, "ok"));
                client.FlushToDisk();
            };

            await client.FlushAsync();

            var remaining = temp.ReadSpool();
            runner.AreEqual(1, remaining.Length, "the event written during the request survives");
            runner.IsTrue(
                remaining[0].Contains(AnalyticsEvents.CaptureStarted, StringComparison.Ordinal),
                "and it is the right one");
        });

        await runner.RunAsync("analytics: opting out discards the identifier and the backlog", async () =>
        {
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            temp.Settings.InstallId = "deadbeef";
            using var client = temp.CreateClient();

            client.Track(AnalyticsEvents.AppStarted);
            client.FlushToDisk();
            runner.AreEqual(1, temp.ReadSpool().Length, "there is a backlog to discard");

            client.SetEnabled(false);

            runner.IsTrue(!File.Exists(temp.SpoolPath), "the pending reports are deleted");
            runner.IsTrue(temp.Settings.InstallId is null, "the installation id is forgotten");
            runner.AreEqual(false, AnalyticsSettingsStore.Load(temp.SettingsPath)!.Enabled, "the choice is persisted");

            client.Track(AnalyticsEvents.CaptureStarted);
            await client.FlushAsync();
            runner.IsTrue(!File.Exists(temp.SpoolPath), "and nothing is collected afterwards");
        });

        await runner.RunAsync("analytics: shutdown writes what is still queued", () =>
        {
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            var client = temp.CreateClient();
            client.Track(AnalyticsEvents.FirstSessionCaptured, (AnalyticsProperties.Result, "ok"));

            // Never flushed explicitly: Dispose is the only thing that can save these.
            client.Dispose();

            var lines = temp.ReadSpool();
            runner.AreEqual(1, lines.Length, "the queued event reaches disk on shutdown");
            runner.IsTrue(lines[0].Contains(AnalyticsEvents.FirstSessionCaptured, StringComparison.Ordinal), "and it is intact");
            return Task.CompletedTask;
        });

        await runner.RunAsync("analytics: a batch that all fails validation does not wedge delivery", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            // Claiming a batch moves the spool aside, and the move only happens when nothing is
            // already claimed. A batch where nothing survives validation must therefore still
            // release the claim, or the spool can never be delivered again - on this run or any
            // later one, because the file outlives the process.
            Directory.CreateDirectory(Path.GetDirectoryName(temp.SpoolPath)!);
            File.WriteAllText(temp.SpoolPath, "{\"name\":\"piper_usage_app_started\",\"time\":\"2026-01-01T00:00:00+00:00\",\"pro\n");

            using var client = temp.CreateClient(server.Endpoint);
            await client.FlushAsync();
            runner.AreEqual(0, server.RequestCount, "there was nothing worth sending");

            client.Track(AnalyticsEvents.CaptureStarted, (AnalyticsProperties.Result, "ok"));
            await client.FlushAsync();

            runner.AreEqual(1, server.RequestCount, "the next real event still gets delivered");
            runner.IsTrue(
                server.LastRequest.Contains(AnalyticsEvents.CaptureStarted, StringComparison.Ordinal),
                "and it is the event that was tracked afterwards");
        });

        await runner.RunAsync("analytics: a null property bag in the spool is survivable", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            // An explicit null deserialises to a null dictionary whatever the initialiser says.
            // Dereferencing it would throw past every catch on the flush path and stop the pump for
            // the life of the process.
            Directory.CreateDirectory(Path.GetDirectoryName(temp.SpoolPath)!);
            File.WriteAllText(temp.SpoolPath,
                "{\"name\":\"piper_usage_app_started\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r\",\"props\":null}\n");

            using var client = temp.CreateClient(server.Endpoint);
            var threw = false;
            try
            {
                await client.FlushAsync();
            }
            catch (Exception ex)
            {
                threw = true;
                runner.IsTrue(false, $"flushing threw {ex.GetType().Name}");
            }

            runner.IsTrue(!threw, "a null property bag does not escape the flush path");
            runner.AreEqual(1, server.RequestCount, "and the event is still delivered");
        });

        await runner.RunAsync("analytics: a tampered installation id is not echoed to the collector", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            // Read back from a user-editable settings file, so it is untrusted like anything else.
            temp.Settings.InstallId = "https://attacker.example/" + new string('x', 5000);

            using var client = temp.CreateClient(server.Endpoint);
            client.Track(AnalyticsEvents.AppStarted);
            await client.FlushAsync();

            runner.AreEqual(1, server.RequestCount, "the batch was delivered");
            runner.IsTrue(
                !server.LastRequest.Contains("attacker.example", StringComparison.Ordinal),
                "the tampered identifier never reaches the payload");
            runner.IsTrue(
                temp.Settings.InstallId is { } minted
                    && AnalyticsSchema.SanitiseValue(minted) == minted,
                "it is replaced with a fresh, valid one");
        });

        await runner.RunAsync("analytics: a partly delivered batch keeps only what did not arrive", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CertTrusted, (AnalyticsProperties.Source, "manual"));
            client.Track(AnalyticsEvents.FirstSessionCaptured);

            // One request per event, so the collector can accept the first and then start refusing.
            server.FailAfter = 1;
            await client.FlushAsync();

            runner.AreEqual(2, server.RequestCount, "delivery stops at the first failure");

            // The delivered event must not be resent, and the rest must not be lost.
            server.FailAfter = int.MaxValue;
            using var nextRun = temp.CreateClient(server.Endpoint);
            await nextRun.FlushAsync();

            var names = server.Requests
                .Where(request => request.Contains("Name=", StringComparison.Ordinal))
                .Select(request => request.Split("Name=")[1].Split('&')[0])
                .ToArray();
            runner.AreEqual(1, names.Count(n => n == AnalyticsEvents.AppStarted), "the delivered event is sent once");
            runner.IsTrue(names.Contains(AnalyticsEvents.CertTrusted), "the failed event is retried");
            runner.IsTrue(names.Contains(AnalyticsEvents.FirstSessionCaptured), "and so is the one behind it");
        });

        await runner.RunAsync("analytics: opting out mid-delivery does not resurrect the batch", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CertTrusted, (AnalyticsProperties.Source, "manual"));

            // The user turns reporting off while the first event is still in the air. Rewriting the
            // remainder afterwards would hand those events back the moment they ever opt in again.
            server.BeforeRespond = () => client.SetEnabled(false);
            server.FailAfter = 1;
            await client.FlushAsync();

            runner.IsTrue(!File.Exists(temp.SpoolPath), "the pending spool is gone");
            runner.IsTrue(!File.Exists(temp.SpoolPath + ".sending"), "and the undelivered remainder is not kept");
        });

        await runner.RunAsync("analytics: opting out stops a batch already being delivered", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            string? machineId = "aaaabbbbccccdddd";
            using var client = temp.CreateClient(
                server.Endpoint, machineId: () => machineId ??= "eeeeffff00001111", forgetMachineId: () => { machineId = null; return true; });

            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CertTrusted, (AnalyticsProperties.Source, "manual"));
            client.Track(AnalyticsEvents.FirstSessionCaptured);

            // The collector keeps accepting, so nothing but the opt-out itself can stop delivery.
            // Consent has to be re-read between events, or the rest of a claimed batch still ships.
            server.BeforeRespond = () => client.SetEnabled(false);
            await client.FlushAsync();

            runner.AreEqual(1, server.RequestCount, "delivery stops at the opt-out, not at the end of the batch");
            runner.IsTrue(machineId is null, "and the identifier is not minted again by the events behind it");
            runner.IsTrue(!File.Exists(temp.SpoolPath), "the pending spool is gone");
            runner.IsTrue(!File.Exists(temp.SpoolPath + ".sending"), "and the undelivered remainder with it");
        });

        await runner.RunAsync("analytics: an oversized spool is discarded rather than read", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            // The UI hands the user this folder, so a file far larger than Piper would ever write is
            // a reachable state. Reading it in full is the thing to avoid.
            Directory.CreateDirectory(Path.GetDirectoryName(temp.SpoolPath)!);
            var line = "{\"name\":\"piper_usage_app_started\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r\",\"props\":{}}\n";
            File.WriteAllText(temp.SpoolPath + ".sending", string.Concat(Enumerable.Repeat(line, 30_000)));
            runner.IsTrue(new FileInfo(temp.SpoolPath + ".sending").Length > 2 * 1024 * 1024, "the spool starts oversized");

            using var client = temp.CreateClient(server.Endpoint);
            await client.FlushAsync();

            runner.AreEqual(0, server.RequestCount, "nothing is sent from it");
            runner.IsTrue(!File.Exists(temp.SpoolPath + ".sending"), "and it is discarded rather than parsed");
        });

        await runner.RunAsync("analytics: one flush cannot make unbounded requests", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            Directory.CreateDirectory(Path.GetDirectoryName(temp.SpoolPath)!);
            var line = "{\"name\":\"piper_usage_app_started\",\"time\":\"2026-01-01T00:00:00+00:00\",\"run\":\"r\",\"props\":{}}\n";
            File.WriteAllText(temp.SpoolPath, string.Concat(Enumerable.Repeat(line, 1_000)));

            using var client = temp.CreateClient(server.Endpoint);
            await client.FlushAsync();

            // The batch length is the number of outbound requests, so the contents of a file must
            // not decide how much traffic one flush generates.
            runner.IsTrue(server.RequestCount is > 0 and <= 200, $"capped at 200 requests, made {server.RequestCount}");

            // Capping the requests must not cost the events behind the cap: the claim takes the whole
            // file, so deleting it on success would destroy them unsent.
            var left = File.Exists(temp.SpoolPath + ".sending")
                ? File.ReadAllLines(temp.SpoolPath + ".sending").Count(l => !string.IsNullOrWhiteSpace(l))
                : 0;
            runner.AreEqual(1_000 - server.RequestCount, left, "everything past the cap is still on disk");

            // And the next flush picks up where this one stopped.
            await client.FlushAsync();
            runner.IsTrue(server.RequestCount > 200, $"the remainder drains on later flushes, now {server.RequestCount}");
        });

        await runner.RunAsync("analytics: opting out forgets the identifier that is actually sent", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            // Stands in for the registry-backed machine identifier, which is what reaches the wire.
            // Stored undashed, as every version before the fix wrote it.
            string? machineId = "aaaabbbbccccddddeeeeffff00001111";
            using var client = temp.CreateClient(
                server.Endpoint, machineId: () => machineId, forgetMachineId: () => { machineId = null; return true; });

            client.Track(AnalyticsEvents.AppStarted);
            await client.FlushAsync();
            runner.IsTrue(
                server.LastRequest.Contains("MUID=aaaabbbb-cccc-dddd-eeee-ffff00001111&", StringComparison.Ordinal),
                "the machine identifier is what gets reported, re-rendered dashed so the collector indexes it");

            client.SetEnabled(false);
            runner.IsTrue(machineId is null, "opting out erases it, not just the installation id");
            runner.IsTrue(temp.Settings.InstallId is null, "and the installation id with it");
        });

        await runner.RunAsync("analytics: an identifier that is not a GUID is never sent", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";

            // Both are user-editable: a registry value and a settings file.
            temp.Settings.InstallId = "not-a-guid";
            using var client = temp.CreateClient(server.Endpoint, machineId: () => "https://bank.example/x");

            client.Track(AnalyticsEvents.AppStarted);
            await client.FlushAsync();

            runner.IsTrue(
                Guid.TryParseExact(temp.Settings.InstallId, "D", out var minted)
                    && server.LastRequest.Contains($"MUID={minted:D}&", StringComparison.Ordinal),
                "a fresh dashed identifier is minted and sent in place of both");
        });

        await runner.RunAsync("analytics: a run reports its start exactly once", async () =>
        {
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient();

            // Three paths legitimately try to record this - startup, agreeing in the consent dialog,
            // and switching reporting on from the Privacy tab - because whichever runs first is the
            // one that finds reporting enabled. Two of them landing would double the funnel's first
            // step and the denominator of the crash rate.
            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CaptureStarted, (AnalyticsProperties.Result, "ok"));
            client.FlushToDisk();

            var spooled = temp.ReadSpool();
            runner.AreEqual(
                1,
                spooled.Count(line => line.Contains(AnalyticsEvents.AppStarted, StringComparison.Ordinal)),
                "the start is recorded once however many times it is reported");
            runner.AreEqual(2, spooled.Length, "and other events are unaffected");
            await Task.CompletedTask;
        });

        await runner.RunAsync("analytics: a refused event is never counted as delivered", async () =>
        {
            using var server = new LoopbackCollector(statusCode: 404);
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CertTrusted, (AnalyticsProperties.Source, "manual"));
            await client.FlushAsync();

            // A wrong contract - an event name or app type the collector will not take - looks
            // exactly like this. Treating the refusal as delivery would bin the events, clear the
            // backoff, and leave Piper making a request per event per flush forever while throwing
            // all of them away, with an empty spool reading as everything working.
            runner.AreEqual(1, server.RequestCount, "delivery stops at the refusal instead of marching on");
            runner.IsTrue(File.Exists(temp.SpoolPath + ".sending"), "the batch behind the refusal is kept");
            runner.AreEqual(
                1,
                File.ReadAllLines(temp.SpoolPath + ".sending").Count(l => l.Length > 0),
                "minus the refused event itself, which would otherwise sit at the head forever");
        });

        await runner.RunAsync("analytics: one refused event does not block the ones behind it", async () =>
        {
            using var server = new LoopbackCollector();
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            client.Track(AnalyticsEvents.AppStarted);
            client.Track(AnalyticsEvents.CertTrusted, (AnalyticsProperties.Source, "manual"));
            client.Track(AnalyticsEvents.FirstSessionCaptured);

            // The collector takes everything except the first request. If a refusal left the event
            // at the head of the file, every later flush would re-claim the same file, be refused on
            // the same line and drop nothing - killing reporting for this install permanently, and
            // across restarts, because the file outlives the process.
            server.RejectFirst = 1;
            await client.FlushAsync();
            await client.FlushAsync();
            await client.FlushAsync();

            runner.IsTrue(!File.Exists(temp.SpoolPath + ".sending"), "the queue drains instead of wedging");
            var names = server.Requests
                .Where(r => r.Contains("Name=", StringComparison.Ordinal))
                .Select(r => r.Split("Name=")[1].Split('&')[0])
                .ToArray();
            runner.IsTrue(names.Contains(AnalyticsEvents.CertTrusted), "the event behind the refusal is delivered");
            runner.IsTrue(names.Contains(AnalyticsEvents.FirstSessionCaptured), "and so is the one behind that");
        });

        await runner.RunAsync("analytics: a collector that refuses everything stops being called", async () =>
        {
            using var server = new LoopbackCollector(statusCode: 400);
            using var temp = new TempAnalytics();
            temp.Settings.Enabled = true;
            temp.Settings.NoticeShownVersion = "0.4.0";
            using var client = temp.CreateClient(server.Endpoint);

            // More events than the collector will be given the chance to refuse, so there is a
            // remainder to look at once delivery gives up.
            client.Track(AnalyticsEvents.AppStarted);
            for (var i = 0; i < 29; i++) client.Track(AnalyticsEvents.FeatureUsed, (AnalyticsProperties.Feature, "find"));

            // Retrying forever would mean a request per event from every opted-in machine for a
            // contract that is never going to work, so a long run of refusals ends delivery.
            for (var attempt = 0; attempt < 40; attempt++) await client.FlushAsync();

            runner.IsTrue(server.RequestCount <= 20, $"stopped after a bounded number of refusals, made {server.RequestCount}");
            runner.IsTrue(File.Exists(temp.SpoolPath + ".sending"), "and what it never got to stays on disk as evidence");
            runner.IsTrue(
                File.ReadAllLines(temp.SpoolPath + ".sending").Count(l => l.Length > 0) >= 10,
                "with the bulk of it intact rather than thrown away one request at a time");
        });

        await runner.RunAsync("analytics: a non-loopback endpoint must be https", () =>
        {
            using var temp = new TempAnalytics();
            var rejected = false;
            try
            {
                using var client = temp.CreateClient(new Uri("http://telemetry.example.com/v1/events"));
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            runner.IsTrue(rejected, "plaintext delivery over the internet is refused");
            return Task.CompletedTask;
        });
    }

    private static string Describe(string? value) => value switch
    {
        null => "null",
        "" => "empty",
        { Length: > 40 } => $"{value.Length} chars",
        _ => value.Replace("\n", "\\n", StringComparison.Ordinal),
    };

    /// <summary>A client pointed at a throwaway directory, so tests never touch the real profile.</summary>
    private sealed class TempAnalytics : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("piper-analytics");

        public AnalyticsSettings Settings { get; } = new();

        public string SpoolPath => Path.Combine(_directory.FullName, "analytics", "pending.jsonl");

        public string SettingsPath => Path.Combine(_directory.FullName, "analytics.json");

        public AnalyticsClient CreateClient(
            Uri? endpoint = null, Func<string?>? machineId = null, Func<bool>? forgetMachineId = null) =>
            new(Settings, "0.0.0-test", endpoint ?? new Uri("https://localhost/v1/events"), SpoolPath, SettingsPath,
                machineId, forgetMachineId);

        public string[] ReadSpool() => File.Exists(SpoolPath)
            ? File.ReadAllLines(SpoolPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray()
            : [];

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Stands in for the collector so the delivery path is exercised without the network.</summary>
    private sealed class LoopbackCollector : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LoopbackCollector(int statusCode = 202)
        {
            StatusCode = statusCode;
            var port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{port}/v1/events");
            _ = Task.Run(ServeAsync);
        }

        public Uri Endpoint { get; }

        public int StatusCode { get; set; }

        public int RequestCount { get; private set; }

        /// <summary>The last request, unescaped - events ride in the query string, not a body.</summary>
        public string LastRequest { get; private set; } = string.Empty;

        /// <summary>Runs while the request is still open, to exercise races against delivery.</summary>
        public Action? BeforeRespond { get; set; }

        /// <summary>Accept this many requests, then refuse - delivery is one request per event.</summary>
        public int FailAfter { get; set; } = int.MaxValue;

        /// <summary>Refuse this many requests with 404 before accepting anything.</summary>
        public int RejectFirst { get; set; }

        /// <summary>Every request seen, unescaped, so a test can assert on what was retried.</summary>
        public List<string> Requests { get; } = [];

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                LastRequest = Uri.UnescapeDataString(context.Request.Url?.PathAndQuery ?? string.Empty);
                Requests.Add(LastRequest);

                RequestCount++;
                BeforeRespond?.Invoke();
                context.Response.StatusCode =
                    RequestCount <= RejectFirst ? 404
                    : RequestCount > FailAfter ? 503
                    : StatusCode;
                context.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _listener.Close();
        }
    }
}
