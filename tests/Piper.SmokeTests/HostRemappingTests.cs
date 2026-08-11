using Piper.Core.Proxy;

internal static class HostRemappingTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("host remapping parses Fiddler and Windows hosts syntax", () =>
    {
        var mappings = HostRemapping.Parse("""
            # A Fiddler-style mapping
            192.0.2.24 api.example.test
            origin.internal api.cname.test
            127.0.0.1 localhost loopback
            invalid-target? ignored.example.test
            """);

        runner.AreEqual("192.0.2.24", mappings["api.example.test"], "IP target");
        runner.AreEqual("origin.internal", mappings["api.cname.test"], "host target");
        runner.AreEqual("127.0.0.1", mappings["loopback"], "Windows hosts alias");
        runner.IsTrue(!mappings.ContainsKey("ignored.example.test"), "malformed target ignored");

        var remapping = new HostRemapping();
        remapping.Apply(new HostRemappingSettings { Enabled = true, Mappings = "new-host.example old-host.example" });
        runner.AreEqual("new-host.example", remapping.Resolve("old-host.example"), "enabled mapping is used");
        remapping.Apply(new HostRemappingSettings { Enabled = false, Mappings = "new-host.example old-host.example" });
        runner.AreEqual("old-host.example", remapping.Resolve("old-host.example"), "disabled mapping leaves DNS target untouched");
        return Task.CompletedTask;
    });
}
