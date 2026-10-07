# Piper.Bench

Measures the proxy from outside it: `piper-bench host` runs a real `ProxyServer` in one process, and the
driver runs a Kestrel origin and a closed-loop load generator in another, so the two do not compete for
CPU. Nothing is installed or trusted: the host's certificate authority is a temporary folder the driver
deletes, and the client trusts it through a callback in its own process.

```powershell
dotnet build tools/Piper.Bench/Piper.Bench.csproj -c Release
dotnet tools/Piper.Bench/bin/Release/net10.0/piper-bench.dll --list
dotnet tools/Piper.Bench/bin/Release/net10.0/piper-bench.dll --runs 15 --out main.jsonl
```

## Scenarios

| Name | Measures |
|---|---|
| `startup` | host process start to the first response through the proxy |
| `get_c1`, `get_c16`, `get_c64` | keep-alive GETs of 256 bytes: `rps`, `p50_ms`, `p99_ms`, proxy `cpu_per_req_us` and `alloc_per_req_bytes`, client and upstream connection counts |
| `hdr_c16` | the same with 40 request and 40 response headers |
| `download_cl`, `download_chunked` | 256 MB download, with Content-Length and chunked: `mbps`, `peak_ws_mb`, `proxy_cpu_ms`, `gc_pause_ms` |
| `upload_64mb` | 64 MB upload |
| `tunnel_ping`, `tunnel_down`, `tunnel_up` | CONNECT tunnel: setup plus a 1 KB round trip; 256 MB each way |
| `sessions_100k` | 100,000 captured sessions: `session_bytes` per session, `heap_after_gc_mb`, `peak_ws_mb` |
| `tls_handshake_30` | HTTPS to 30 new hosts: first request (CONNECT, both handshakes, a minted certificate) against a repeat on the same connection |
| `h2_c100` | HTTP/2 through the proxy, one client connection, 100 parallel streams; a response that is not HTTP/2 counts as an error |
| `rtt50_h1`, `rtt50_h2` | up to 1 GB streamed from an origin 50 ms away (the proxy-to-origin leg goes through a delaying relay; `rtt_actual_ms` is the measured round trip), read for `max(--duration, 10 s)` |

Every scenario run starts a fresh host process and warms it up unmeasured. Tunnel payloads are 1 KB,
never a few bytes: ESET buffers loopback HTTP and stalls tiny payloads with or without Piper.

## Not covered

HTTP/3 (needs QUIC on both sides), the AutoResponder, the session grid and inspector, and start-up to
first paint of the WinForms app: the tool drives the proxy library, not the UI. The 50 ms delay is
applied only to the proxy-to-origin leg, in each direction 25 ms, by a relay in the driver; the client
to proxy leg is loopback.

## Reading results

Metric names carry their unit (`_ms`, `_us`, `_mb`, `_bytes` lower is better; `rps`, `mbps` higher is
better). `--compare` reports the change in the medians and `OVERLAP` when the min..max ranges share a
point; trust a difference only when they do not, and with at least 8 runs (15 for large transfers).
