using Piper.Core.Proxy;

internal static class ConnectionSettingsBlobTests
{
    // The DefaultConnectionSettings value Windows 11 writes for a machine with no proxy: version,
    // change counter, DIRECT flag, three empty strings and a 32 byte trailer.
    private static readonly byte[] DirectValue =
    [
        0x46, 0x00, 0x00, 0x00,
        0x9c, 0x01, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        .. new byte[32],
    ];

    public static Task RunAsync(TestRunner runner) => runner.RunAsync("WinINET connection settings round trip", () =>
    {
        runner.IsTrue(ConnectionSettingsBlob.TryParse(DirectValue, out var direct), "a real direct-connection value parses");
        runner.AreEqual(0x46, direct.Version, "version");
        runner.AreEqual(412, direct.Counter, "change counter");
        runner.AreEqual(ConnectionSettingsBlob.DirectFlag, direct.Flags, "direct flag");
        runner.AreEqual(string.Empty, direct.ProxyServer, "no proxy server");
        runner.AreEqual(32, direct.Trailer.Length, "trailer carried through");
        runner.AreEqual(Convert.ToHexString(DirectValue), Convert.ToHexString(direct.ToBytes()),
            "parsing and writing it back is byte for byte identical");

        // Enabling has to leave WPAD and PAC settings alone: they are the user's, and only the
        // manual proxy is Piper's to change.
        var withAutoDetect = direct with { Flags = ConnectionSettingsBlob.DirectFlag | ConnectionSettingsBlob.AutoDetectFlag };
        var enabled = withAutoDetect.WithProxy("127.0.0.1:8888", "<local>") with { Counter = 413 };

        runner.IsTrue(ConnectionSettingsBlob.TryParse(enabled.ToBytes(), out var reread), "the enabled value parses back");
        runner.AreEqual("127.0.0.1:8888", reread.ProxyServer, "proxy endpoint");
        runner.AreEqual("<local>", reread.ProxyBypass, "bypass list");
        runner.AreEqual(413, reread.Counter, "counter");
        runner.IsTrue((reread.Flags & ConnectionSettingsBlob.ProxyFlag) != 0, "proxy flag set");
        runner.IsTrue((reread.Flags & ConnectionSettingsBlob.DirectFlag) == 0, "direct flag cleared");
        runner.IsTrue((reread.Flags & ConnectionSettingsBlob.AutoDetectFlag) != 0, "auto-detect preserved");
        runner.AreEqual(32, reread.Trailer.Length, "trailer still there");

        // Restoring writes the captured bytes back untouched apart from the counter, so the value
        // Piper puts back is the one it found.
        var restored = ConnectionSettingsBlob.WithCounter(DirectValue, 414);
        runner.IsTrue(ConnectionSettingsBlob.TryParse(restored, out var restoredBlob), "the restored value parses");
        runner.AreEqual(414, restoredBlob.Counter, "counter moved forward");
        runner.AreEqual(Convert.ToHexString(DirectValue.AsSpan(8)), Convert.ToHexString(restored.AsSpan(8)),
            "everything after the counter is untouched");
        runner.AreEqual(412, ConnectionSettingsBlob.ReadCounter(DirectValue), "the source value was not modified");

        // Anything unrecognised must be rejected rather than half-parsed - a wrong guess would be
        // written straight into the user's network configuration.
        runner.IsTrue(!ConnectionSettingsBlob.TryParse(null, out _), "a missing value is rejected");
        runner.IsTrue(!ConnectionSettingsBlob.TryParse([0x46, 0x00], out _), "a truncated value is rejected");
        runner.IsTrue(!ConnectionSettingsBlob.TryParse([.. DirectValue.AsSpan(0, 20)], out _),
            "a value cut off mid-string is rejected");
        runner.IsTrue(!ConnectionSettingsBlob.TryParse(
            [0x46, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0xff, 0xff, 0xff, 0x7f], out _),
            "an absurd string length is rejected");
        runner.AreEqual(0, ConnectionSettingsBlob.ReadCounter([0x46]), "an unreadable counter reads as zero");

        return Task.CompletedTask;
    });
}
