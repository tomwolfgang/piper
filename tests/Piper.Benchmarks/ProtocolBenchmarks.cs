using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Http2.Hpack;

namespace Piper.Benchmarks;

/// <summary>Parsing a request and a response head from a stream of bytes already in memory. Each
/// operation includes making the <see cref="HttpStreamReader"/>, as the proxy does per connection.</summary>
[MemoryDiagnoser]
public class HttpParserBenchmarks
{
    private byte[] _request = [];
    private byte[] _response = [];

    [GlobalSetup]
    public void Setup()
    {
        var request = new StringBuilder("GET /api/orders?id=42&expand=items HTTP/1.1\r\nHost: api.example.com\r\n");
        var response = new StringBuilder("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n");
        for (var i = 0; i < 14; i++)
        {
            request.Append($"X-Header-{i}: value-{i}-{new string('v', 30)}\r\n");
            response.Append($"X-Response-{i}: value-{i}-{new string('v', 30)}\r\n");
        }
        _request = Encoding.ASCII.GetBytes(request.Append("\r\n").ToString());
        _response = Encoding.ASCII.GetBytes(response.Append("\r\n").ToString());
    }

    [Benchmark]
    public async Task<HttpRequestData?> RequestHead()
    {
        using var reader = new HttpStreamReader(new MemoryStream(_request, writable: false));
        return await HttpParser.ReadRequestHeadAsync(reader, CancellationToken.None);
    }

    [Benchmark]
    public async Task<HttpResponseData> ResponseHead()
    {
        using var reader = new HttpStreamReader(new MemoryStream(_response, writable: false));
        return (await HttpParser.ReadResponseHeadAsync(reader, "GET", CancellationToken.None)).Head;
    }
}

/// <summary>Decoding a header block that <see cref="HpackEncoder"/> wrote, and the Huffman coder alone.</summary>
[MemoryDiagnoser]
public class HpackBenchmarks
{
    private readonly HpackDecoder _decoder = new();
    private byte[] _block = [];
    private byte[] _huffman = [];

    [Params(32, 512)]
    public int HuffmanLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        List<(string, string)> fields =
        [
            (":method", "GET"), (":scheme", "https"), (":authority", "api.example.com"), (":path", "/api/orders?id=42"),
            ("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0 Safari/537.36"),
            ("accept", "application/json, text/plain, */*"), ("accept-encoding", "gzip, deflate, br"),
            ("cookie", "session=" + new string('a', 120) + "; theme=dark"), ("authorization", "Bearer " + new string('t', 100)),
        ];
        _block = HpackEncoder.Encode(fields);
        _huffman = Huffman.Encode(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("www.example.com/path?q=", 40))[..HuffmanLength]));
    }

    [Benchmark]
    public List<(string Name, string Value)> DecodeHeaderBlock() => _decoder.Decode(_block);

    [Benchmark]
    public byte[] HuffmanDecode() => Huffman.Decode(_huffman);
}

/// <summary>Reading a stream of 16 KB DATA frames, the shape of a large HTTP/2 download.</summary>
[MemoryDiagnoser]
public class Http2FrameReaderBenchmarks
{
    private byte[] _frames = [];

    [GlobalSetup]
    public async Task Setup()
    {
        using var stream = new MemoryStream();
        await Http2FrameWriter.WriteDataAsync(stream, 1, new byte[1 << 20], endStream: true, peerMaxFrameSize: 16_384, CancellationToken.None);
        _frames = stream.ToArray();
    }

    [Benchmark]
    public async Task<int> ReadAllFrames()
    {
        using var stream = new MemoryStream(_frames, writable: false);
        var frames = 0;
        while (await Http2FrameReader.ReadAsync(stream, 16_384, CancellationToken.None) is not null) frames++;
        return frames;
    }
}

/// <summary>Decoding a 1 MB response body (<see cref="ContentCodec.Decode"/> does not use the cache).</summary>
[MemoryDiagnoser]
public class ContentCodecBenchmarks
{
    private readonly Dictionary<string, byte[]> _bodies = [];

    [Params("gzip", "deflate", "br")]
    public string Encoding { get; set; } = "gzip";

    [GlobalSetup]
    public void Setup()
    {
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 20_000).Select(i => $"{{\"id\":{i},\"name\":\"item-{i % 97}\",\"tags\":[\"a\",\"b\"]}},\n")));
        _bodies["gzip"] = Compress(text, s => new GZipStream(s, CompressionLevel.Fastest, leaveOpen: true));
        _bodies["deflate"] = Compress(text, s => new ZLibStream(s, CompressionLevel.Fastest, leaveOpen: true));
        _bodies["br"] = Compress(text, s => new BrotliStream(s, CompressionLevel.Fastest, leaveOpen: true));
    }

    private static byte[] Compress(byte[] data, Func<Stream, Stream> wrap)
    {
        using var output = new MemoryStream();
        using (var compressor = wrap(output)) compressor.Write(data);
        return output.ToArray();
    }

    [Benchmark]
    public byte[] Decode() => ContentCodec.Decode(_bodies[Encoding], Encoding);
}
