using Piper.Core.Http;
using Piper.Core.Sessions;

internal static class SessionStoreAdmissionTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("session admission excludes out-of-scope traffic", () =>
    {
        var store = new SessionStore
        {
            CaptureFilter = session => session.ProcessName == "browser",
        };

        store.Add(new Session { ProcessName = "worker" });
        runner.AreEqual(0, store.Count, "process scope rejects sessions before they are counted");

        var browser = new Session { ProcessName = "browser" };
        store.Add(browser);
        runner.AreEqual(1, store.Count, "in-scope process is collected");

        var composed = new Session { IsComposed = true, ProcessName = "Piper (composer)" };
        store.Add(composed);
        runner.AreEqual(2, store.Count, "composer sends bypass the traffic capture scope");

        var responseFiltered = new SessionStore
        {
            CompletedSessionFilter = session => session.Response?.StatusCode is >= 200 and < 300,
        };
        var success = new Session();
        responseFiltered.Add(success);
        runner.AreEqual(0, responseFiltered.Count, "response filter defers pending sessions");

        success.Response = new HttpResponseData { StatusCode = 200 };
        success.Completed = DateTimeOffset.Now;
        responseFiltered.NotifyUpdated(success);
        runner.AreEqual(1, responseFiltered.Count, "matching completed response is collected");

        var failure = new Session();
        responseFiltered.Add(failure);
        failure.Response = new HttpResponseData { StatusCode = 404 };
        failure.Completed = DateTimeOffset.Now;
        responseFiltered.NotifyUpdated(failure);
        runner.AreEqual(1, responseFiltered.Count, "non-matching completed response is never counted");

        var composedWithFilteredResponse = new Session { IsComposed = true };
        responseFiltered.Add(composedWithFilteredResponse);
        runner.AreEqual(2, responseFiltered.Count, "composer sends appear before a response filter can exclude them");

        return Task.CompletedTask;
    });
}
