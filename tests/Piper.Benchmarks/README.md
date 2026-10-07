# Piper.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org) micro-benchmarks of `Piper.Core`'s hot paths. On demand
only: the project is **not** in `Piper.slnx`, so `eng/verify.ps1`, CI, CodeQL and the installer never
restore or build it, and nothing ships it. For whole-proxy numbers (requests per second, downloads,
TLS) use `tools/Piper.Bench` instead.

```powershell
dotnet run -c Release --project tests/Piper.Benchmarks -- --list flat
dotnet run -c Release --project tests/Piper.Benchmarks -- --filter "*HttpParser*"
dotnet run -c Release --project tests/Piper.Benchmarks -- --filter "*" --exporters json   # everything, BenchmarkDotNet.Artifacts/
```

| Class | Measures |
|---|---|
| `HttpParserBenchmarks` | request and response head parse (15 headers), including making the stream reader |
| `HpackBenchmarks` | decoding a 9-field header block; `Huffman.Decode` of 32 and 512 bytes |
| `Http2FrameReaderBenchmarks` | reading 64 DATA frames of 16 KB (1 MB) |
| `ContentCodecBenchmarks` | `ContentCodec.Decode` of a 1 MB body, gzip / deflate / br |
| `SearchQueryBenchmarks` | `SearchQuery.Matches` over 100,000 sessions, four queries |
| `SessionStoreBenchmarks` | `SessionStore.Add` of 100,000 sessions, `CopyTo` of the full store |
| `CertificateBenchmarks` | `CertificateAuthority.GetCertificateFor` for a new host (RSA-2048, one shared leaf key) against the same certificate with a shared ECDSA key, a fresh ECDSA key and a fresh RSA key per host |

Notes:

- The benchmarks run in this process (`InProcessNoEmit`) rather than in a generated project that
  BenchmarkDotNet would restore and build over the network, so a run downloads and builds nothing.
- The certificate benchmarks make their authority in a temporary folder, deleted afterwards. They
  never use the real one and install nothing. Windows deletes each leaf's temporary key container
  when the certificate is released; a run killed halfway can leave a few small key files behind.
- Close other programs first and trust a difference only when the intervals in the table do not
  overlap. The same rules as `tools/Piper.Bench` apply.
