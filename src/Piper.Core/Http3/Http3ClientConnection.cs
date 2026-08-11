using System.Net;
using System.Net.Quic;
using System.Net.Security;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Http3.Qpack;
using Piper.Core.Proxy;

// CA1416 flags System.Net.Quic as platform-specific (linux/macOS/windows). Every entry point here
// is gated behind IsSupported -> QuicConnection.IsSupported, which is the runtime check the
// platform analyser cannot see through, and h3 is opt-in and falls back to TCP when unavailable.
#pragma warning disable CA1416

namespace Piper.Core.Http3;

/// <summary>
/// Client-role HTTP/3 connection (origin-facing). QUIC itself comes from
/// <see cref="System.Net.Quic"/> -- msquic ships inside the .NET runtime, so this needs no NuGet
/// package and nothing extra installed on an end user's machine. What is implemented here is only
/// the HTTP/3 layer above it: the control stream, SETTINGS, framing and QPACK.
/// </summary>
/// <remarks>
/// Upstream only, by design. A browser pointed at a system HTTP proxy always tunnels through
/// <c>CONNECT</c> over TCP and disables QUIC for proxied traffic, so there is no such thing as a
/// browser speaking HTTP/3 *to* Piper. This exists so Piper can see what an origin actually
/// serves over QUIC, which is otherwise invisible.
/// <para>
/// Like <see cref="Http2ClientConnection"/>, phase 1 scope is one request per connection: no
/// stream reuse, no server push (we never send MAX_PUSH_ID, so a compliant origin cannot push).
/// </para>
/// </remarks>
public sealed class Http3ClientConnection : IAsyncDisposable
{
    private const long MaxFieldSection = 128 * 1024;
    private const long MaxBodyBytes = 256L * 1024 * 1024;

    private readonly QuicConnection _connection;
    private QuicStream? _controlStream;

    private Http3ClientConnection(QuicConnection connection) => _connection = connection;

    public IPEndPoint? RemoteEndpoint => _connection.RemoteEndPoint as IPEndPoint;

    /// <summary>True when QUIC is usable at all on this machine. False means no msquic, and every
    /// h3 attempt should be skipped rather than repeatedly failing.</summary>
    public static bool IsSupported => QuicConnection.IsSupported;

    public static async Task<Http3ClientConnection> ConnectAsync(
        string host, int port, ProxyOptions options, CancellationToken ct)
    {
        var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(host, port),
            DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
            DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
            MaxInboundUnidirectionalStreams = 8, // control + QPACK encoder/decoder, plus slack
            MaxInboundBidirectionalStreams = 0,  // we never accept origin-initiated requests
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ApplicationProtocols = [new SslApplicationProtocol("h3")],
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    !options.ValidateUpstreamCertificates || errors == SslPolicyErrors.None,
            },
        }, ct).ConfigureAwait(false);

        var client = new Http3ClientConnection(connection);
        try
        {
            await client.SendControlStreamAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return client;
    }

    /// <summary>
    /// Opens the outbound control stream and sends SETTINGS. RFC 9114 §6.2.1 requires each side to
    /// open exactly one control stream, whose first byte is the stream type, and to send SETTINGS
    /// as its first frame -- an origin will close the connection if this never arrives.
    /// </summary>
    private async Task SendControlStreamAsync(CancellationToken ct)
    {
        _controlStream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct).ConfigureAwait(false);

        var preamble = new List<byte>(32);
        VarInt.Write(preamble, Http3StreamType.Control);
        preamble.AddRange(Http3FrameWriter.Encode(Http3FrameType.Settings, Http3FrameWriter.EncodeSettings(
            // Zero capacity and zero blocked streams tell the origin's encoder it may not use the
            // dynamic table, which is what lets the QPACK decoder here stay static-table-only.
            (Http3SettingId.QpackMaxTableCapacity, 0),
            (Http3SettingId.QpackBlockedStreams, 0),
            (Http3SettingId.MaxFieldSectionSize, MaxFieldSection))));

        await _controlStream.WriteAsync(preamble.ToArray(), ct).ConfigureAwait(false);
        await _controlStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<HttpResponseData> SendRequestAsync(HttpRequestData request, CancellationToken ct)
    {
        // The origin opens its own control and QPACK streams; nothing on them matters to a
        // static-table-only decoder, but they must still be drained or QUIC flow control on those
        // streams eventually stalls the connection.
        var draining = Task.Run(() => DrainInboundStreamsAsync(ct), CancellationToken.None);

        await using var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);

        var fields = Http2MessageAdapter.ToHeaderFields(request); // h3 reuses h2's pseudo-header shape (RFC 9114 §4.1.1)
        var headerBlock = QpackEncoder.Encode(fields);

        await stream.WriteAsync(Http3FrameWriter.Encode(Http3FrameType.Headers, headerBlock),
            completeWrites: request.Body.Length == 0, ct).ConfigureAwait(false);

        if (request.Body.Length > 0)
        {
            await stream.WriteAsync(Http3FrameWriter.Encode(Http3FrameType.Data, request.Body),
                completeWrites: true, ct).ConfigureAwait(false);
        }

        var response = await ReadResponseAsync(stream, ct).ConfigureAwait(false);

        // The drain loop only ends with the connection; it is not part of request completion.
        _ = draining;
        return response;
    }

    private static async Task<HttpResponseData> ReadResponseAsync(QuicStream stream, CancellationToken ct)
    {
        var reader = new Http3StreamReader(stream);
        var body = new MemoryStream();
        List<(string Name, string Value)>? fields = null;

        while (true)
        {
            var frame = await reader.ReadFrameAsync(MaxBodyBytes, ct).ConfigureAwait(false);
            if (frame is null) break; // origin finished the stream

            switch (frame.Value.Type)
            {
                case Http3FrameType.Headers:
                    var decoded = QpackDecoder.Decode(frame.Value.Payload.Span);
                    // 1xx are interim (RFC 9114 §4.1.2): keep reading for the real response.
                    var status = decoded.FirstOrDefault(f => f.Name == ":status").Value;
                    if (int.TryParse(status, out var code) && code is >= 100 and < 200) continue;
                    // A second HEADERS after the response is a trailer section; ignore it.
                    fields ??= decoded;
                    break;

                case Http3FrameType.Data:
                    body.Write(frame.Value.Payload.Span);
                    break;

                default:
                    break; // unknown / reserved frame types must be ignored (§9)
            }
        }

        if (fields is null) throw new HttpParseException("HTTP/3 response ended before its headers arrived.");

        var response = Http2MessageAdapter.ToResponse(fields);
        response.HttpVersion = "HTTP/3";
        response.Body = body.ToArray();
        return response;
    }

    /// <summary>Accepts and discards the origin's unidirectional streams. Their contents are
    /// irrelevant here -- with a zero-capacity dynamic table the QPACK encoder stream carries
    /// nothing we need -- but leaving them unread would eventually apply back pressure.</summary>
    private async Task DrainInboundStreamsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = await _connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var s = stream;
                        var scratch = new byte[4096];
                        while (await s.ReadAsync(scratch, ct).ConfigureAwait(false) > 0) { }
                    }
                    catch { /* the peer closing a stream we do not act on is not an error */ }
                }, CancellationToken.None);
            }
        }
        catch { /* connection closed, or shutting down */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_controlStream is not null) await _controlStream.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
