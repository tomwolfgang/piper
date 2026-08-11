using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using Piper.Core.Http;
using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>
/// Forwards one HTTP/2 stream's request to its origin and returns the response, recording the
/// exchange as a <see cref="Session"/> exactly like the HTTP/1.1 path does. Each call opens its
/// own fresh upstream connection -- HTTP/2 streams are not pooled across requests in phase 1,
/// mirroring the existing Composer/<see cref="RequestExecutor"/> pattern rather than inventing a
/// second, harder concurrency-safe pooling problem in the riskiest part of this feature.
/// </summary>
internal static class Http2RequestForwarder
{
    public static async Task<HttpResponseData> ForwardAsync(
        HttpRequestData request, ProxyOptions options, SessionStore store, Http3.AltSvcCache altSvc,
        string clientEndpoint, string processName, CancellationToken ct)
    {
        var session = new Session
        {
            Request = request,
            IsHttps = true,
            ClientEndpoint = clientEndpoint,
            ProcessName = processName,
            State = SessionState.SendingRequest,
        };
        store.Add(session);

        var url = request.Url;
        if (url is null)
        {
            var badRequest = HttpResponseData.Simple(400, "Bad Request",
                "Piper could not determine the target URL for this HTTP/2 request.");
            FinishFailed(session, store, "No resolved URL.", badRequest);
            return badRequest;
        }

        var host = url.Host;
        var port = url.Port;
        var stopwatch = Stopwatch.StartNew();
        UpstreamConnection? upstream = null;

        try
        {
            var outbound = ProxyServer.BuildOutboundRequest(request, preserveUpgrade: false, options);
            var beforeResponse = stopwatch.Elapsed;

            void MarkSent()
            {
                session.State = SessionState.AwaitingResponse;
                store.NotifyUpdated(session);
                beforeResponse = stopwatch.Elapsed;
            }

            // HTTP/3 first when the origin advertised it; null means fall back to TCP.
            var response = await Http3Attempt.TryFetchAsync(outbound, url, options, altSvc, MarkSent, ct).ConfigureAwait(false);

            if (response is null)
            {
                var connectStart = stopwatch.Elapsed;
                upstream = await UpstreamConnection.ConnectAsync(host, port, isTls: true, options, ct).ConfigureAwait(false);
                session.ConnectTime = stopwatch.Elapsed - connectStart;
                session.ServerEndpoint = upstream.RemoteEndpoint;

                response = await UpstreamRequestSender.SendAsync(upstream, outbound, MarkSent, ct).ConfigureAwait(false);
            }

            session.TimeToFirstByte = stopwatch.Elapsed - beforeResponse;
            altSvc.RecordAltSvc(host, response.Headers["Alt-Svc"]);

            // response.HttpVersion is left exactly as it came from the upstream leg (HttpParser's
            // literal status line for h1.1, or "HTTP/2" from Http2ClientConnection) -- it is
            // deliberately NOT overwritten here. Request and Response each record the version of
            // the leg they actually travelled: the browser's choice for the request, the real
            // origin's choice for the response. That is the whole point of a debugging proxy that
            // translates between protocol versions.
            var inbound = ProxyServer.BuildInboundResponse(response, clientWantsClose: true);
            inbound.Headers.Remove("Connection"); // downstream-wire plumbing; h2 has no such header at all
            session.Response = inbound;
            session.State = SessionState.Complete;
            session.Completed = DateTimeOffset.Now;
            session.InvalidateSearchIndex();
            store.NotifyUpdated(session);
            return inbound;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or HttpParseException or OperationCanceledException)
        {
            var detail = ProxyServer.Describe(ex);
            var failure = HttpResponseData.Simple(502, "Bad Gateway", $"Piper could not reach {host}:{port}.\r\n\r\n{detail}");
            FinishFailed(session, store, detail, failure);
            return failure;
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private static void FinishFailed(Session session, SessionStore store, string error, HttpResponseData response)
    {
        session.Response = response;
        session.State = SessionState.Failed;
        session.Error = error;
        session.Completed = DateTimeOffset.Now;
        session.InvalidateSearchIndex();
        store.NotifyUpdated(session);
    }
}
