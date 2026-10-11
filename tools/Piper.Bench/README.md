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
| `h2_c100` | HTTP/2 through the proxy, one client connection, 100 parallel streams, 2,000 requests; a response that is not HTTP/2 counts as an error |
| `rtt50_h1`, `rtt50_h2` | up to 1 GB streamed from an origin 50 ms away (the proxy-to-origin leg goes through a delaying relay; `rtt_actual_ms` is the measured round trip), read for `max(--duration, 10 s)`; a response that ends early with fewer bytes is a failure, not a rate |

Every scenario run starts a fresh host process and warms it up unmeasured. Tunnel payloads are 1 KB,
never a few bytes: ESET buffers loopback HTTP and stalls tiny payloads with or without Piper.

`rtt50_h1` and `rtt50_h2` are not a protocol-only comparison: the HTTP/2 run is HTTPS, so the proxy also
decrypts it. `tls_handshake_30` leaves the client's own certificate check out of the first request
(`harness_chain_p50_ms`). `h2_c100` makes the proxy open one upstream connection per request (a known gap),
and each closed one holds a loopback port in TIME_WAIT for about two minutes, so before a run it waits (up to
150 s) until fewer than 2,500 such sockets to the origin remain, and fails with that reason instead of recording
a run the exhausted ports ruined.

## Not covered

HTTP/3 (needs QUIC on both sides), the AutoResponder, the session grid and inspector, and start-up to
first paint of the WinForms app: the tool drives the proxy library, not the UI. The 50 ms delay is
applied only to the proxy-to-origin leg, in each direction 25 ms, by a relay in the driver; the client
to proxy leg is loopback.

## Reading results

Metric names carry their unit (`_ms`, `_us`, `_mb`, `_bytes` lower is better; `rps`, `mbps` higher is
better). `--compare` reports the change in the medians and `OVERLAP` when the min..max ranges share a
point; trust a difference only when they do not. A "better" or "worse" verdict needs at least 4 runs per
build and carries `[n<5]` below 5; use 5 or more (15 for large transfers).

A load run with more than 1% of its requests failed or timed out, or none succeeded, is a failure: it is
left out of every median and listed in the summary with its error count.

## Results files

`--out` names a new file (default `piper-bench-<UTC time>.jsonl`); an existing file is never overwritten, and a
device name or unwritable folder is refused before any host starts. Schema 2 is made to be shared: an `env`
line, a `build` line (configuration, runtime, GC, CPU, power plan, commit of the driver's checkout), then one
line per run, with no user, machine or folder name and no path (hosts by label and file name, paths removed
from errors, only known noisy processes named). `--host label=path` **runs** that file with your rights: name
only builds you made or trust.
