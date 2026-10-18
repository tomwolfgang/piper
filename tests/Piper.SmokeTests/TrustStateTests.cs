using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Piper.Core.Proxy;
using Piper.Core.Security;

// C1: what Piper does and says while its root certificate is not trusted. The Windows store is never
// touched: the trust check runs against a fake store, and every file lives in the per-run temp folder.
internal static class TrustStateTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await RunSuspendedOptionAsync(runner);
        await RunStoreReadDisposesAsync(runner);
        await RunCachedCheckAsync(runner);
    }

    private static Task RunSuspendedOptionAsync(TestRunner runner) => runner.RunAsync("suspending decryption keeps the user's choice and is never saved", () =>
    {
        var options = new ProxyOptions();
        runner.IsTrue(options.ShouldDecrypt("example.test"), "by default HTTPS is decrypted");
        options.DecryptionSuspended = true;
        runner.IsTrue(!options.ShouldDecrypt("example.test"), "a suspended proxy decrypts nothing");
        runner.IsTrue(options.DecryptHttps, "the user's Decrypt HTTPS choice is left alone");
        runner.IsTrue(!JsonText(ProxyConfigurationSettings.From(options)).Contains("Suspended", StringComparison.Ordinal),
            "the suspension is never saved");
        options.DecryptionSuspended = false;
        runner.IsTrue(options.ShouldDecrypt("example.test"), "and decryption resumes when it is lifted");
        return Task.CompletedTask;
    });

    private static string JsonText(ProxyConfigurationSettings settings) => System.Text.Json.JsonSerializer.Serialize(settings);

    private static Task RunStoreReadDisposesAsync(TestRunner runner) => runner.RunAsync("the trust check finds by thumbprint and disposes every certificate it reads", () =>
    {
        using var target = NewCertificate("CN=Piper trust target");
        using var other = NewCertificate("CN=Piper trust other");
        var read = new List<X509Certificate2>();

        var trusted = TrustStore.IsTrusted(target.Thumbprint, location => new FakeSource(read, other, target));
        runner.IsTrue(trusted, "a root with the thumbprint is trusted");
        runner.AreEqual(2, read.Count, "the first store held the root, so the second store was not read");
        runner.IsTrue(read.All(certificate => certificate.Handle == IntPtr.Zero),
            "every certificate the store returned was disposed (no native handle left behind)");

        read.Clear();
        var missing = TrustStore.IsTrusted(target.Thumbprint, location => new FakeSource(read, other));
        runner.IsTrue(!missing, "a root that is in neither store is not trusted");
        runner.IsTrue(read.Count > 0 && read.All(certificate => certificate.Handle == IntPtr.Zero),
            "the certificates read while finding nothing were disposed as well");

        read.Clear();
        var locations = new List<StoreLocation>();
        var machineOnly = TrustStore.IsTrusted(target.Thumbprint, location =>
        {
            locations.Add(location);
            return location == StoreLocation.CurrentUser ? throw new UnauthorizedAccessException() : new FakeSource(read, target);
        });
        runner.IsTrue(machineOnly && locations.Count == 2, "a denied store is skipped and the other one is still read");
        runner.IsTrue(read.All(certificate => certificate.Handle == IntPtr.Zero), "disposal holds on that path too");
        return Task.CompletedTask;
    });

    private static Task RunCachedCheckAsync(TestRunner runner) => runner.RunAsync("the trust check is cached and refreshed after trust or remove", () =>
    {
        var trusted = false;
        var clock = new ManualClock();
        var cache = new TrustStateCache(() => trusted, TimeSpan.FromSeconds(30), clock);

        var alwaysUntrusted = true;
        for (var i = 0; i < 120; i++)
        {
            alwaysUntrusted &= !cache.IsTrusted;
            clock.Advance(TimeSpan.FromSeconds(0.2)); // 24 seconds of one status update per 200 ms
        }

        runner.IsTrue(alwaysUntrusted, "an untrusted root reads as untrusted every time");

        runner.AreEqual(1, cache.QueryCount, "24 seconds of status updates reads the store once, not once per update");

        trusted = true; // the user clicks Trust: the owner invalidates
        runner.IsTrue(!cache.IsTrusted, "without invalidation the cached answer is kept");
        cache.Invalidate();
        runner.IsTrue(cache.IsTrusted, "after Invalidate the new state is read at once");
        runner.AreEqual(2, cache.QueryCount, "Invalidate costs exactly one read");

        trusted = false; // the user clicks Remove
        cache.Invalidate();
        runner.IsTrue(!cache.IsTrusted, "Remove is seen at once after Invalidate");

        trusted = true; // trusted outside Piper: noticed when the cached answer ages out
        clock.Advance(TimeSpan.FromSeconds(31));
        runner.IsTrue(cache.IsTrusted, "a change made outside Piper is noticed once the answer is older than its lifetime");
        runner.AreEqual(4, cache.QueryCount, "and that took one more read");

        // An infinite lifetime means read on demand only: it never ages out, however long the clock runs.
        var never = new TrustStateCache(() => true, Timeout.InfiniteTimeSpan, clock);
        _ = never.IsTrusted;
        clock.Advance(TimeSpan.FromDays(3650));
        runner.IsTrue(!never.NeedsRead && never.QueryCount == 1, "an infinite lifetime never ages out");
        never.Invalidate();
        runner.IsTrue(never.NeedsRead, "but Invalidate still forces a read");

        // The timer path asks without reading: it sees the last answer and learns a read is due.
        clock.Advance(TimeSpan.FromSeconds(31));
        runner.IsTrue(cache.NeedsRead && cache.Last == true, "an aged-out answer is still available, and flagged as due for a read");
        var before = cache.QueryCount;
        _ = cache.Last;
        _ = cache.NeedsRead;
        runner.AreEqual(before, cache.QueryCount, "looking at the last answer never reads the store");

        // A trust change that lands while a read is under way must not be lost to that read.
        var racing = (TrustStateCache?)null;
        racing = new TrustStateCache(() => { racing!.Invalidate(); return true; }, TimeSpan.FromSeconds(30), clock);
        _ = racing.IsTrusted;
        runner.IsTrue(racing.NeedsRead, "a read overtaken by Invalidate is not kept as fresh");

        // A slow read that started before the user clicked Remove must not put the old answer back.
        var store = true;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var slow = new TrustStateCache(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var seen = Volatile.Read(ref store);
                started.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return seen; // the answer as it was when this read began
            }

            return Volatile.Read(ref store);
        }, TimeSpan.FromSeconds(30), clock);
        var worker = Task.Run(() => slow.IsTrusted);
        started.Wait(TimeSpan.FromSeconds(10));
        Volatile.Write(ref store, false); // the user removes the root
        slow.Invalidate();
        runner.IsTrue(!slow.IsTrusted, "the read after the user's click sees the new state");
        release.Set();
        runner.IsTrue(worker.GetAwaiter().GetResult(), "the stale worker still returns what it read to its own caller");
        runner.IsTrue(slow.Last == false && !slow.IsTrusted, "but it does not overwrite the newer answer, in Last or in the next read");
        return Task.CompletedTask;
    });

    private static X509Certificate2 NewCertificate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>A root store that hands out fresh copies on every read, like the real one, and records them.</summary>
    private sealed class FakeSource(List<X509Certificate2> read, params X509Certificate2[] contents) : TrustStore.IRootCertificateSource
    {
        public X509Certificate2Collection Certificates
        {
            get
            {
                var copies = new X509Certificate2Collection();
                foreach (var certificate in contents)
                {
                    var copy = X509CertificateLoader.LoadCertificate(certificate.RawData);
                    read.Add(copy);
                    copies.Add(copy);
                }

                return copies;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
