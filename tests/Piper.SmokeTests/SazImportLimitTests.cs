using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Sessions;

/// <summary>The limits on one import, the bad-archive paths, and what the store does with imported sessions.</summary>
internal static class SazImportLimitTests
{
    private const string Get = "GET https://api.example.test/v1/items HTTP/1.1\r\nHost: api.example.test\r\n\r\n";
    private const string Ok = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nok";

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("SAZ import rejects an archive that inflates past the total cap", () =>
        {
            var limits = new SazImportLimits { MaxTotalBytes = 1024 * 1024, MaxEntryBytes = 1024 * 1024 };
            var path = Save(archive =>
            {
                for (var i = 1; i <= 4; i++)
                    SazImportHardeningTests.AddZeros(archive, $"raw/{i}_c.txt", "GET https://api.example.test/x HTTP/1.1\r\nHost: api.example.test\r\n\r\n", 400 * 1024);
            });
            try
            {
                var result = SazImporter.Import(path, limits);
                runner.AreEqual(SazImportFailure.TooLarge, result.Failure, "the archive is refused as too large");
                runner.AreEqual(0, result.Sessions.Count, "an archive that breaks a limit as a whole imports nothing");
                runner.IsTrue(result.Warnings.Count >= 1, "and says why");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import skips an entry over the per-entry cap and keeps the rest", () =>
        {
            var limits = new SazImportLimits { MaxEntryBytes = 1024 * 1024 };
            var path = Save(archive =>
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", Get);
                SazImportHardeningTests.AddZeros(archive, "raw/2_c.txt", "GET https://api.example.test/big HTTP/1.1\r\nHost: api.example.test\r\n\r\n", 3L * 1024 * 1024);
                SazImportHardeningTests.Add(archive, "raw/3_c.txt", Get);
            });
            try
            {
                var result = SazImporter.Import(path, limits);
                runner.AreEqual(2, result.Sessions.Count, "the two small sessions import");
                runner.AreEqual(SazImportFailure.None, result.Failure, "one oversize entry does not reject the archive");
                runner.AreEqual(1, result.Warnings.Count, "the oversize entry is reported once");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import caps the number of entries", () =>
        {
            var limits = new SazImportLimits { MaxEntries = 50 };

            var atCap = Save(archive => { for (var i = 1; i <= 50; i++) SazImportHardeningTests.Add(archive, $"raw/{i}_c.txt", Get); });
            var overCap = Save(archive => { for (var i = 1; i <= 51; i++) SazImportHardeningTests.Add(archive, $"raw/{i}_c.txt", Get); });
            try
            {
                var accepted = SazImporter.Import(atCap, limits);
                runner.AreEqual(50, accepted.Sessions.Count, "an archive exactly at the cap imports");

                var refused = SazImporter.Import(overCap, limits);
                runner.AreEqual(SazImportFailure.TooManyEntries, refused.Failure, "one entry over the cap refuses the archive");
                runner.AreEqual(0, refused.Sessions.Count, "and imports nothing");
            }
            finally
            {
                File.Delete(atCap);
                File.Delete(overCap);
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import stops reading a central directory that is larger than any honest one", () =>
        {
            // Entry count is not known until the runtime has built an object for every header, so
            // the directory itself is what has to be bounded. 3,000 entries is about 250 KB of it.
            var limits = new SazImportLimits { MaxCentralDirectoryBytes = 32 * 1024 };
            var path = Save(archive =>
            {
                for (var i = 1; i <= 3_000; i++)
                    archive.CreateEntry($"raw/padding-entry-with-a-longish-name-{i}.txt");
            });
            try
            {
                var result = SazImporter.Import(path, limits);
                runner.AreEqual(SazImportFailure.TooManyEntries, result.Failure, "the oversized directory is refused");
                runner.AreEqual(0, result.Sessions.Count, "and nothing is imported");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import reports files that are not archives instead of throwing", () =>
        {
            var garbage = SazImportHardeningTests.Write(new byte[8192]);
            var empty = SazImportHardeningTests.Write([]);
            var text = SazImportHardeningTests.Write(Encoding.ASCII.GetBytes("this is not a zip file at all"));
            var whole = SazImportHardeningTests.Zip(archive => SazImportHardeningTests.Add(archive, "raw/1_c.txt", Get));
            var truncated = SazImportHardeningTests.Write(whole[..(whole.Length - 30)]);
            var missing = Path.Combine(Path.GetTempPath(), $"piper-saz-missing-{Guid.NewGuid():N}.saz");
            try
            {
                foreach (var (label, path) in new[] { ("zero bytes", garbage), ("an empty file", empty), ("text", text), ("a truncated archive", truncated), ("a missing file", missing) })
                {
                    var result = SazImporter.Import(path);
                    runner.AreEqual(SazImportFailure.Unreadable, result.Failure, $"{label} is unreadable");
                    runner.AreEqual(0, result.Sessions.Count, $"{label} imports nothing");
                    runner.IsTrue(result.Warnings.Count >= 1, $"{label} is explained");
                }
            }
            finally
            {
                foreach (var path in new[] { garbage, empty, text, truncated }) File.Delete(path);
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import skips entries the runtime cannot inflate", () =>
        {
            // Each of these throws from Open() or Read(), not from parsing.
            var unsupported = SazImportHardeningTests.Zip(FillThree);
            BinaryPrimitives.WriteUInt16LittleEndian(unsupported.AsSpan(SazImportHardeningTests.CentralHeader(unsupported, "raw/2_c.txt") + 10), 99);

            var encrypted = SazImportHardeningTests.Zip(FillThree);
            encrypted[SazImportHardeningTests.CentralHeader(encrypted, "raw/2_c.txt") + 8] |= 0x01;

            var corrupt = SazImportHardeningTests.Zip(archive =>
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", Get);
                using (var stream = archive.CreateEntry("raw/2_c.txt", CompressionLevel.Optimal).Open())
                    stream.Write(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello piper ", 600))));
                SazImportHardeningTests.Add(archive, "raw/3_c.txt", Get);
            });
            // The first entry's data is followed by the second entry's local header, then its data.
            var second = SazImportHardeningTests.CentralHeader(corrupt, "raw/2_c.txt");
            var dataStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(corrupt.AsSpan(second + 42)) + 30 + "raw/2_c.txt".Length;
            for (var i = dataStart + 8; i < dataStart + 64; i++) corrupt[i] ^= 0xFF;

            foreach (var (label, bytes) in new[] { ("an unsupported compression method", unsupported), ("an encrypted entry", encrypted), ("a corrupt deflate stream", corrupt) })
            {
                var path = SazImportHardeningTests.Write(bytes);
                try
                {
                    var result = SazImporter.Import(path);
                    runner.AreEqual(SazImportFailure.None, result.Failure, $"{label} does not reject the archive");
                    runner.IsTrue(result.Sessions.Count is >= 2 and <= 3, $"{label}: the other entries still import ({result.Sessions.Count})");
                }
                finally { File.Delete(path); }
            }

            return Task.CompletedTask;

            static void FillThree(ZipArchive archive)
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", Get);
                SazImportHardeningTests.Add(archive, "raw/2_c.txt", Get);
                SazImportHardeningTests.Add(archive, "raw/3_c.txt", Get);
            }
        });

        await runner.RunAsync("SAZ import keeps only a prefix of a large body, as a capture does", () =>
        {
            var path = Save(archive =>
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", "POST https://api.example.test/upload HTTP/1.1\r\nHost: api.example.test\r\n\r\n" + new string('a', 100));
                SazImportHardeningTests.Add(archive, "raw/1_s.txt", "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\n" + new string('b', 100));
            });
            try
            {
                var clipped = SazImporter.Import(path, new SazImportLimits { MaxBodyBytes = 10 }).Sessions[0];
                runner.AreEqual(10, clipped.Request!.Body.Length, "the request body is cut to the limit");
                runner.AreEqual(100L, clipped.Request.BodyTotalLength, "and still reports the length it had");
                runner.IsTrue(!clipped.Request.IsBodyComplete, "so it is not passed off as the whole body");
                runner.AreEqual(10, clipped.Response!.Body.Length, "the response body is cut too");
                runner.AreEqual(100L, clipped.Response.BodyTotalLength, "with its true length kept");

                var result = SazImporter.Import(path, new SazImportLimits { MaxBodyBytes = 10 });
                runner.AreEqual(2, result.Warnings.Count, "each cut is reported");

                var whole = SazImporter.Import(path, new SazImportLimits { MaxBodyBytes = 0 }).Sessions[0];
                runner.AreEqual(100, whole.Request!.Body.Length, "0 keeps every body");
                runner.IsTrue(whole.Request.IsBodyComplete, "which stays complete");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import refuses an oversize header block without scanning it all", () =>
        {
            var hugeHead = "GET https://api.example.test/x HTTP/1.1\r\nHost: api.example.test\r\n" + string.Concat(Enumerable.Repeat("X-Padding: 1234567890\r\n", 10_000)) + "\r\n";
            // A folded line is appended to the previous value, so a head of them is quadratic to parse.
            var folded = "GET https://api.example.test/x HTTP/1.1\r\nHost: api.example.test\r\nX-Long: a\r\n" + string.Concat(Enumerable.Repeat(" b\r\n", 16_000)) + "\r\n";
            var path = Save(archive =>
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", Get);
                SazImportHardeningTests.Add(archive, "raw/2_c.txt", hugeHead);
                SazImportHardeningTests.Add(archive, "raw/3_c.txt", folded);
            });
            try
            {
                var watch = Stopwatch.StartNew();
                var result = SazImporter.Import(path);
                watch.Stop();

                runner.AreEqual(2, result.Sessions.Count, "the oversize head is skipped; the folded one, inside the limit, imports");
                runner.IsTrue(result.Warnings.Count >= 1, "the skipped head is reported");
                runner.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"and it takes {watch.Elapsed.TotalSeconds:F1}s, not minutes");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import bounds how many warnings it produces", () =>
        {
            var path = Save(archive =>
            {
                for (var i = 1; i <= 1_000; i++) SazImportHardeningTests.Add(archive, $"raw/{i}_c.txt", "not an http message");
            });
            try
            {
                var result = SazImporter.Import(path);
                runner.AreEqual(0, result.Sessions.Count, "every entry is unusable");
                runner.AreEqual(SazImporter.MaxWarnings + 1, result.Warnings.Count, "the warnings are capped, with one line for the rest");
                runner.IsTrue(result.Warnings[^1].Contains("further", StringComparison.Ordinal), "which says how many were left out");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import marks its sessions as imported", () =>
        {
            var path = Save(archive =>
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", Get);
                SazImportHardeningTests.Add(archive, "raw/1_s.txt", Ok);
            });
            try
            {
                var session = SazImporter.Import(path).Sessions.Single();
                runner.IsTrue(session.IsImported, "an imported session says so");
                runner.IsTrue(!session.IsComposed, "and is not a Composer send");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("imported sessions are admitted under a capture scope and a filterset", () =>
        {
            // The scope rejects everything by process name and the filterset rejects everything by
            // response, which is what "Fiddler SAZ" and an active Use Filters do to an import.
            var store = new SessionStore
            {
                CaptureFilter = _ => false,
                CompletedSessionFilter = _ => false,
            };

            var path = Save(archive =>
            {
                for (var i = 1; i <= 5; i++)
                {
                    SazImportHardeningTests.Add(archive, $"raw/{i}_c.txt", Get);
                    SazImportHardeningTests.Add(archive, $"raw/{i}_s.txt", Ok);
                }
            });
            try
            {
                var imported = SazImporter.Import(path).Sessions;
                var retained = store.AddRange(imported);
                runner.AreEqual(5, retained, "AddRange reports every import as retained");
                runner.AreEqual(5, store.Count, "and the store holds them all");

                var single = SazImporter.Import(path).Sessions[0];
                store.Add(single);
                runner.AreEqual(6, store.Count, "Add honours the same bypass");

                var captured = new Session { Request = new HttpRequestData(), Completed = DateTimeOffset.Now };
                store.Add(captured);
                runner.AreEqual(6, store.Count, "live traffic is still refused by the same filters");
                runner.AreEqual(0, store.AddRange([captured, new Session { Completed = DateTimeOffset.Now }]), "AddRange counts none of the refused sessions");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("AddRange returns what the store actually kept", () =>
        {
            var store = new SessionStore { Capacity = 3 };
            var raised = new List<int>();
            store.SessionAdded += (_, e) => raised.Add(e.Session.Id);

            var batch = Enumerable.Range(0, 5).Select(_ => new Session { IsImported = true, Completed = DateTimeOffset.Now }).ToList();
            var retained = store.AddRange(batch);

            runner.AreEqual(3, retained, "five sessions into a store of three keep three");
            runner.AreEqual(3, store.Count, "the count agrees");
            runner.AreEqual(batch[2].Id, store.Snapshot()[0].Id, "the newest of the batch are the ones kept");
            runner.AreEqual(3, raised.Count, "SessionAdded is raised for what is kept, not for what was trimmed");
            runner.IsTrue(store.FindById(batch[0].Id) is null, "a trimmed session is not addressable");

            // Existing sessions make way for a batch that fits.
            var second = new SessionStore { Capacity = 4 };
            second.Add(new Session { Completed = DateTimeOffset.Now });
            second.Add(new Session { Completed = DateTimeOffset.Now });
            runner.AreEqual(3, second.AddRange(batch.Take(3).ToList()), "a batch that fits is fully retained");
            runner.AreEqual(4, second.Count, "by evicting the oldest existing sessions");

            var deferred = new SessionStore { CompletedSessionFilter = _ => true };
            var pending = new Session();
            runner.AreEqual(0, deferred.AddRange([pending]), "a session still waiting for its response is not counted");
            pending.Completed = DateTimeOffset.Now;
            deferred.NotifyUpdated(pending);
            runner.AreEqual(1, deferred.Count, "it is admitted when it completes");

            runner.AreEqual(0, new SessionStore().AddRange([]), "an empty batch adds nothing");

            return Task.CompletedTask;
        });

        await runner.RunAsync("AddRange applies the body budget once, not once per session", () =>
        {
            // Session by session, every add rescans the released prefix from the front: quadratic in the
            // size of the archive, all under the store's lock.
            var store = new SessionStore { Capacity = 0, RetainedBodyBudgetBytes = 1024 * 1024 };
            var batch = new List<Session>(60_000);
            for (var i = 0; i < 60_000; i++)
            {
                var response = new HttpResponseData { Body = new byte[1024] };
                batch.Add(new Session { IsImported = true, Response = response, Completed = DateTimeOffset.Now });
            }

            var watch = Stopwatch.StartNew();
            var retained = store.AddRange(batch);
            watch.Stop();

            runner.AreEqual(60_000, retained, "every session is kept");
            var held = store.Snapshot().Sum(s => s.Response!.Body.LongLength);
            runner.IsTrue(held <= 1024 * 1024, $"bodies are released down to the budget (held {held:N0})");
            runner.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"in {watch.Elapsed.TotalSeconds:F2}s");

            return Task.CompletedTask;
        });
    }

    private static string Save(Action<ZipArchive> fill) => SazImportHardeningTests.Write(SazImportHardeningTests.Zip(fill));
}
