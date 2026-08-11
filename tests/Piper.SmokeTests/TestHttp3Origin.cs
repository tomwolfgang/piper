using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Http3;
using Piper.Core.Http3.Qpack;

// A real QUIC listener speaking HTTP/3 on loopback. Outbound UDP/443 is blocked on this network,
// so public h3 origins are unreachable -- and a loopback origin is the better test anyway: it is
// deterministic, offline, and lets a test assert on exactly what the origin received.
//
// This is the server side of HTTP/3, which Piper itself never implements (h3 is upstream-only), so
// unlike TestHttp2Origin it is genuinely independent of the code under test rather than the same
// codec paired with itself -- stronger evidence that Http3ClientConnection is on-spec.
#pragma warning disable CA1416 // guarded by QuicListener.IsSupported at every call site

internal sealed class TestHttp3Origin : IAsyncDisposable
{
    private readonly QuicListener _listener;
    private readonly Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public static bool IsSupported => QuicListener.IsSupported;

    private TestHttp3Origin(QuicListener listener, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
    {
        _listener = listener;
        _handler = handler;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port => _listener.LocalEndPoint.Port;

    public static async Task<TestHttp3Origin> StartAsync(
        X509Certificate2 certificate, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
    {
        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [new SslApplicationProtocol("h3")],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
                DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = [new SslApplicationProtocol("h3")],
                },
            }),
        });

        return new TestHttp3Origin(listener, handler);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            QuicConnection connection;
            try { connection = await _listener.AcceptConnectionAsync(_cts.Token); }
            catch { return; }

            _ = Task.Run(() => ServeConnectionAsync(connection));
        }
    }

    private async Task ServeConnectionAsync(QuicConnection connection)
    {
        try
        {
            // Our own control stream + SETTINGS, as RFC 9114 6.2.1 requires of both peers.
            var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, _cts.Token);
            var preamble = new List<byte>();
            VarInt.Write(preamble, Http3StreamType.Control);
            preamble.AddRange(Http3FrameWriter.Encode(Http3FrameType.Settings,
                Http3FrameWriter.EncodeSettings((Http3SettingId.QpackMaxTableCapacity, 0), (Http3SettingId.QpackBlockedStreams, 0))));
            await control.WriteAsync(preamble.ToArray(), _cts.Token);
            await control.FlushAsync(_cts.Token);

            // Drain whatever the client opens (its control and QPACK streams).
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var incoming = await connection.AcceptInboundStreamAsync(_cts.Token);
                        if (incoming.Type == QuicStreamType.Bidirectional) _ = Task.Run(() => ServeRequestAsync(incoming));
                        else _ = Task.Run(async () =>
                        {
                            try { var buf = new byte[1024]; while (await incoming.ReadAsync(buf, _cts.Token) > 0) { } }
                            catch { }
                        });
                    }
                }
                catch { }
            });

            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch { /* connection closed or shutting down */ }
        finally { await connection.DisposeAsync(); }
    }

    private async Task ServeRequestAsync(QuicStream stream)
    {
        try
        {
            await using var s = stream;
            var reader = new Http3StreamReader(s);

            List<(string Name, string Value)>? fields = null;
            var body = new MemoryStream();

            while (true)
            {
                var frame = await reader.ReadFrameAsync(64L * 1024 * 1024, _cts.Token);
                if (frame is null) break;

                if (frame.Value.Type == Http3FrameType.Headers) fields ??= QpackDecoder.Decode(frame.Value.Payload.Span);
                else if (frame.Value.Type == Http3FrameType.Data) body.Write(frame.Value.Payload.Span);
            }

            if (fields is null) return;

            var request = Http2MessageAdapter.ToRequest(fields);
            request.Body = body.ToArray();

            var response = await _handler(request, _cts.Token);

            var headerBlock = QpackEncoder.Encode(Http2MessageAdapter.ToHeaderFields(response));
            await s.WriteAsync(Http3FrameWriter.Encode(Http3FrameType.Headers, headerBlock),
                completeWrites: response.Body.Length == 0, _cts.Token);

            if (response.Body.Length > 0)
            {
                // Chunked deliberately: a single giant DATA frame would not exercise the client's
                // multi-frame reassembly, which is where a real origin's behaviour lives.
                const int chunk = 16 * 1024;
                for (var offset = 0; offset < response.Body.Length; offset += chunk)
                {
                    var size = Math.Min(chunk, response.Body.Length - offset);
                    var last = offset + size >= response.Body.Length;
                    await s.WriteAsync(Http3FrameWriter.Encode(Http3FrameType.Data, response.Body.AsSpan(offset, size)),
                        completeWrites: last, _cts.Token);
                }
            }
        }
        catch { /* the test asserts on the client side */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _listener.DisposeAsync(); } catch { }
        try { await _acceptLoop; } catch { }
    }
}
