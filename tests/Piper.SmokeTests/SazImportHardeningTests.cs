using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Piper.Core.Sessions;

/// <summary>
/// A SAZ or RAZ archive is a file someone sent us, so everything in it -- entry names, the sizes its
/// central directory claims, how far an entry really inflates -- is hostile input.
/// </summary>
internal static class SazImportHardeningTests
{
    private const string Get = "GET https://api.example.test/v1/items HTTP/1.1\r\nHost: api.example.test\r\n\r\n";
    private const string Ok = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nok";

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("SAZ import skips entries whose session number is not an ASCII number that fits", () =>
        {
            // \d matches every Unicode decimal digit, and int.Parse throws on both those and on eleven
            // digits. The exception escaped the importer and, from an async void handler, killed the app.
            var path = Write(Zip(archive =>
            {
                Add(archive, "raw/1_c.txt", Get);
                Add(archive, "raw/1_s.txt", Ok);
                Add(archive, "raw/١_c.txt", Get);                 // ARABIC-INDIC DIGIT ONE
                Add(archive, "raw/１２_c.txt", Get);           // FULLWIDTH DIGITS ONE TWO
                Add(archive, "raw/12345678901_c.txt", Get);            // 11 digits: overflows int
                Add(archive, "raw/9999999999_c.txt", Get);             // 10 digits: outside the accepted range
                Add(archive, "raw/999999999_c.txt", Get);              // 9 digits: the largest accepted
                Add(archive, "raw/-3_c.txt", Get);
                Add(archive, "raw/_c.txt", Get);
            }));
            try
            {
                var result = SazImporter.Import(path);
                runner.AreEqual(2, result.Sessions.Count, "the two well-formed numbers import; every other name is skipped");
                runner.AreEqual(SazImportFailure.None, result.Failure, "bad names never reject the archive");
                runner.IsTrue(result.Warnings.Count >= 1, "skipped entries are reported");
                runner.IsTrue(result.Warnings.All(w => w.Length < 400), "a warning does not echo an unbounded entry name");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import does not size a buffer from a central directory that lies", () =>
        {
            var bytes = Zip(archive =>
            {
                Add(archive, "raw/1_c.txt", Get);
                Add(archive, "raw/1_s.txt", Ok);
                Add(archive, "raw/2_c.txt", Get);
            });
            // Entry 2 claims to inflate to almost 2 GB. Sizing a MemoryStream from that used to
            // allocate all of it from a file of a few hundred bytes.
            PatchUncompressedSize(bytes, "raw/2_c.txt", 0x7FFF_FFFF);
            var path = Write(bytes);
            try
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var result = SazImporter.Import(path);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                runner.IsTrue(allocated < 64L * 1024 * 1024, $"the import allocated {allocated:N0} bytes, not gigabytes");
                runner.AreEqual(1, result.Sessions.Count, "the honest entry still imports");
                runner.IsTrue(result.Warnings.Count >= 1, "the lying entry is reported");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import survives a central directory that under-reports what inflates", () =>
        {
            // The opposite lie: a small claim over a body that is really large. Whatever the runtime
            // does with it (truncate, refuse), the importer must not keep the excess or throw.
            var bytes = Zip(archive =>
            {
                Add(archive, "raw/1_c.txt", Get);
                AddZeros(archive, "raw/2_c.txt", "POST https://api.example.test/x HTTP/1.1\r\nHost: api.example.test\r\n\r\n", 3L * 1024 * 1024);
            });
            PatchUncompressedSize(bytes, "raw/2_c.txt", 200);
            var path = Write(bytes);
            try
            {
                var result = SazImporter.Import(path);
                runner.IsTrue(result.Sessions.Count >= 1, "the honest entry imports");
                runner.IsTrue(result.Sessions.All(s => (s.Request?.Body.LongLength ?? 0) <= 200),
                    "no session carries the excess the directory did not admit to");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await runner.RunAsync("SAZ import refuses an entry that inflates past the per-entry cap", () =>
        {
            // 129 MiB of zeros is a few hundred KB on disk; the cap is 128 MiB.
            var path = Write(Zip(archive =>
            {
                Add(archive, "raw/1_c.txt", Get);
                Add(archive, "raw/1_s.txt", Ok);
                AddZeros(archive, "raw/2_c.txt", "POST https://api.example.test/x HTTP/1.1\r\nHost: api.example.test\r\n\r\n", 129L * 1024 * 1024);
            }));
            try
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var result = SazImporter.Import(path);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                runner.AreEqual(1, result.Sessions.Count, "only the small session imports");
                runner.IsTrue(result.Warnings.Count >= 1, "the oversize entry is reported");
                runner.IsTrue(allocated < 400L * 1024 * 1024, $"the bomb was not inflated whole ({allocated:N0} bytes allocated)");
            }
            finally { File.Delete(path); }

            return Task.CompletedTask;
        });

        await SazImportLimitTests.RunAsync(runner);
    }

    // ------------------------------------------------------------------ helpers

    internal static byte[] Zip(Action<ZipArchive> fill)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true)) fill(archive);
        return memory.ToArray();
    }

    internal static string Write(byte[] archive)
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-saz-hardening-{Guid.NewGuid():N}.saz");
        File.WriteAllBytes(path, archive);
        return path;
    }

    internal static void Add(ZipArchive archive, string name, string text)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.Latin1.GetBytes(text));
    }

    /// <summary>An entry of <paramref name="head"/> followed by <paramref name="zeroBytes"/> zero bytes,
    /// written in slices so the test never holds the body itself.</summary>
    internal static void AddZeros(ZipArchive archive, string name, string head, long zeroBytes)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(Encoding.Latin1.GetBytes(head));
        var slice = new byte[1024 * 1024];
        for (var written = 0L; written < zeroBytes; written += slice.Length)
            stream.Write(slice, 0, (int)Math.Min(slice.Length, zeroBytes - written));
    }

    /// <summary>The offset of the central directory header for <paramref name="name"/>.</summary>
    internal static int CentralHeader(byte[] bytes, string name)
    {
        for (var i = 0; i + 46 <= bytes.Length; i++)
        {
            if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B || bytes[i + 2] != 0x01 || bytes[i + 3] != 0x02) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28));
            if (Encoding.UTF8.GetString(bytes, i + 46, nameLength) == name) return i;
        }
        throw new InvalidOperationException($"No central directory header for {name}.");
    }

    internal static void PatchUncompressedSize(byte[] bytes, string name, uint claimed) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(CentralHeader(bytes, name) + 24), claimed);
}
