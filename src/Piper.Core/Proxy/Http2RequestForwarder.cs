using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using Piper.Core.Http;
using Piper.Core.Http2;
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
    public static async Task<Http2StreamResponse> ForwardAsync(
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

        // Same insertion point as the HTTP/1.1 path: the session exists, the URL is resolved, and
        // nothing has gone upstream yet.
        var decision = options.AutoResponder.Evaluate(session);
        if (decision.Outcome is not AutoResponderOutcome.Passthrough)
        {
            if (decision.Delay > TimeSpan.Zero) await Task.Delay(decision.Delay, ct).ConfigureAwait(false);

            if (decision.Outcome is AutoResponderOutcome.Drop or AutoResponderOutcome.Reset)
            {
                FinishAborted(session, store, decision);

                // Cancel/InternalError, never RefusedStream: RFC 9113 declares that one explicitly
                // retry-safe, so browsers would silently re-issue the request and the rule would look
                // as though it never fired.
                throw new Http2StreamAbortException(decision.Outcome == AutoResponderOutcome.Drop
                    ? Http2ErrorCode.Cancel
                    : Http2ErrorCode.InternalError);
            }

            if (decision.Outcome == AutoResponderOutcome.Respond)
            {
                var canned = await decision.Action!
                    .BuildResponseAsync(decision.Rule!, decision.Match, request, options.MaxBodyBytes, ct)
                    .ConfigureAwait(false);

                var faked = ProxyServer.BuildInboundResponse(canned, bodyIsAuthoritative: true, clientWantsClose: true);
                faked.Headers.Remove("Connection"); // h2 has no such header at all
                session.Response = faked;
                session.AutoResponderRule = decision.Description;
                session.State = SessionState.Complete;
                session.Completed = DateTimeOffset.Now;
                session.InvalidateSearchIndex();
                store.NotifyUpdated(session);
                return faked;
            }

            if (decision.Outcome == AutoResponderOutcome.Redirect
                && decision.Action!.ResolveTarget(decision.Match, url) is { } target)
            {
                session.AutoResponderRule = decision.Description;
                url = target;
            }
        }

        var host = url.Host;
        var port = url.Port;
        var stopwatch = Stopwatch.StartNew();
        UpstreamConnection? upstream = null;

        // Set when the upstream leg left its body on the connection for relaying; null when the
        // whole message is already in hand, as it is over HTTP/2 and HTTP/3.
        HttpBodyDescriptor? framing = null;
        HttpStreamReader? bodyReader = null;

        try
        {
            var outbound = ProxyServer.BuildOutboundRequest(request, preserveUpgrade: false, options);
            if (!ReferenceEquals(url, request.Url))
            {
                outbound.Url = url;
                outbound.Headers.Set("Host", url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}");
            }
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

                var sent = await UpstreamRequestSender.SendAsync(upstream, outbound, MarkSent, ct).ConfigureAwait(false);
                response = sent.Head;
                framing = sent.IsBuffered ? null : sent.Body;
                bodyReader = sent.BodyReader;
            }

            session.TimeToFirstByte = stopwatch.Elapsed - beforeResponse;
            if (url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) altSvc.RecordAltSvc(host, port, response.Headers["Alt-Svc"]);

            // response.HttpVersion is left exactly as it came from the upstream leg (HttpParser's
            // literal status line for h1.1, or "HTTP/2" from Http2ClientConnection) -- it is
            // deliberately NOT overwritten here. Request and Response each record the version of
            // the leg they actually travelled: the browser's choice for the request, the real
            // origin's choice for the response. That is the whole point of a debugging proxy that
            // translates between protocol versions.
            var canHaveBody = HttpParser.ResponseCanHaveBody(request.Method, response.StatusCode);
            var inbound = ProxyServer.BuildInboundResponse(
                response, framing is null && canHaveBody, clientWantsClose: true);
            inbound.Headers.Remove("Connection"); // downstream-wire plumbing; h2 has no such header at all

            // Beside chunked, or on a body read until close, the origin's Content-Length does not
            // describe the bytes about to be relayed, and an h2 client checks DATA against it.
            if (framing is { Framing: HttpBodyFraming.Chunked or HttpBodyFraming.UntilClose or HttpBodyFraming.StreamEnd })
                inbound.Headers.Remove("Content-Length");
            session.Response = inbound;
            session.InvalidateSearchIndex();

            // Nothing to relay: the body is already in hand (HTTP/3), or there is none at all (HEAD,
            // 204, 304), which ends on the HEADERS frame as it does on h1.
            if (framing is not { Framing: not HttpBodyFraming.None } body)
            {
                // The client is sent the whole body; the capture keeps only as much of it as a
                // relayed body would.
                if (inbound.Body.LongLength > options.MaxCapturedBodyBytes)
                {
                    var kept = inbound.Clone();
                    kept.KeepPrefix(options.MaxCapturedBodyBytes);
                    session.Response = kept;
                }

                session.State = SessionState.Complete;
                session.Completed = DateTimeOffset.Now;
                store.NotifyUpdated(session);
                return inbound;
            }

            // The body is still on the upstream connection, so it is relayed into the HTTP/2 stream
            // as it arrives rather than read here first. The connection has to outlive this method
            // for that, so ownership of it moves into the relay and the finally below lets it go.
            var leg = upstream!;
            upstream = null;
            store.NotifyUpdated(session);

            return new Http2StreamResponse(inbound, async (destination, relayCt) =>
            {
                try
                {
                    // Already cancelled when the head could not be sent, or when the client reset
                    // the stream or the connection closed first. A body that happened to be
                    // buffered whole must not then be recorded as delivered.
                    if (relayCt.IsCancellationRequested)
                        throw new OperationCanceledException("The stream ended before the body was relayed.", relayCt);

                    // Here rather than before returning: past the check above, the connection has
                    // queued the HEADERS frame, so only now is the head with the client.
                    ProxyServer.EnterReceivingBody(session, body);
                    store.NotifyUpdated(session);

                    var relayed = await HttpBodyRelay.RelayAsync(
                        bodyReader!, body, destination,
                        rechunkDownstream: false, options.MaxCapturedBodyBytes, session.ReportBytesReceived, relayCt)
                        .ConfigureAwait(false);

                    inbound.Body = relayed.Captured;
                    inbound.BodyTotalLength = relayed.TotalBytes;
                    session.State = SessionState.Complete;
                }
                catch (Exception relayError) when (relayError is SocketException or IOException
                                                       or HttpParseException or Http2ProtocolException or OperationCanceledException)
                {
                    // The head is already with the client and cannot be taken back. Rethrown so the
                    // stream is reset rather than ended: a clean END_STREAM would pass a short body
                    // off as a complete one.
                    session.State = SessionState.Failed;
                    session.Error = ProxyServer.Describe(relayError);
                    throw;
                }
                finally
                {
                    leg.Dispose();

                    // Anything the catch above does not expect still ends the body, so the session
                    // cannot be left looking as though it were arriving.
                    if (session.State == SessionState.ReceivingBody) session.State = SessionState.Failed;
                    session.Completed = DateTimeOffset.Now;
                    session.InvalidateSearchIndex();
                    store.NotifyUpdated(session);
                }
            });
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or HttpParseException or Http2ProtocolException or OperationCanceledException)
        {
            var detail = ProxyServer.Describe(ex);
            var failure = ex is ProxyLoopException loop
                ? loop.ToResponse()
                : HttpResponseData.Simple(502, "Bad Gateway", $"Piper could not reach {host}:{port}.\r\n\r\n{detail}");
            FinishFailed(session, store, detail, failure);
            return failure;
        }
        finally
        {
            upstream?.Dispose();
        }
    }

    private static void FinishAborted(Session session, SessionStore store, AutoResponderDecision decision)
    {
        session.AutoResponderRule = decision.Description;
        session.State = SessionState.Failed;
        session.Error = $"AutoResponder rule '{decision.Description}' aborted the stream.";
        session.Completed = DateTimeOffset.Now;
        session.InvalidateSearchIndex();
        store.NotifyUpdated(session);
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
