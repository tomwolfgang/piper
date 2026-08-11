using Piper.Core.Proxy;

internal static class ProxyConfigurationSettingsStoreTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("proxy configuration settings persist and apply", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-proxy-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new ProxyConfigurationSettings
            {
                DecryptHttps = false,
                EnableHttp2Downstream = false,
                EnableHttp2Upstream = false,
                EnableHttp3Upstream = true,
                GlobalUserAgent = "Piper smoke test",
                HostRemapping = new HostRemappingSettings
                {
                    Enabled = true,
                    Mappings = "127.0.0.1 api.example.test",
                },
            };
            ProxyConfigurationSettingsStore.Save(saved, path);
            var restored = ProxyConfigurationSettingsStore.Load(path);

            runner.IsTrue(restored is not null, "saved settings can be loaded");
            runner.AreEqual(saved.DecryptHttps, restored!.DecryptHttps, "HTTPS decryption");
            runner.AreEqual(saved.EnableHttp2Downstream, restored.EnableHttp2Downstream, "downstream HTTP/2");
            runner.AreEqual(saved.EnableHttp2Upstream, restored.EnableHttp2Upstream, "upstream HTTP/2");
            runner.AreEqual(saved.EnableHttp3Upstream, restored.EnableHttp3Upstream, "upstream HTTP/3");
            runner.AreEqual(saved.GlobalUserAgent, restored.GlobalUserAgent, "global User-Agent");
            runner.AreEqual(saved.HostRemapping.Enabled, restored.HostRemapping.Enabled, "host remapping enabled");
            runner.AreEqual(saved.HostRemapping.Mappings, restored.HostRemapping.Mappings, "host remappings");

            var options = new ProxyOptions();
            restored.ApplyTo(options);
            runner.AreEqual(saved.GlobalUserAgent, options.GlobalUserAgent, "User-Agent applies to proxy options");
            runner.IsTrue(options.HostRemapping.Enabled, "host remapping applies to proxy options");
            runner.AreEqual("127.0.0.1", options.HostRemapping.Resolve("api.example.test"), "mapped host resolves to IP");

            File.WriteAllText(path, "not json");
            runner.AreEqual<ProxyConfigurationSettings?>(null, ProxyConfigurationSettingsStore.Load(path), "corrupt settings are ignored");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
