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

A host `.dll` is run with the `dotnet.exe` that runs the driver, else the one in `DOTNET_HOST_PATH`, else the
registered install (64-bit, then 32-bit registry view) or the default one, always by full path: a `dotnet.exe` in the current directory is never started.

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
| `h2_c100` | HTTP/2 through the proxy, one client connection, 100 parallel streams, 2,000 measured requests (or `--duration`, if that ends first; the ramp and the first stats round trip before the window do not count toward the 2,000, but are capped at 2,000 of their own, so `requests` is 2,000 unless some failed and the connections to the origin stay under 4,000); a response that is not HTTP/2 counts as an error |
| `rtt50_h1`, `rtt50_h2` | up to 1 GB streamed from an origin 50 ms away (the proxy-to-origin leg goes through a delaying relay; `rtt_actual_ms` is the measured round trip), read for `max(--duration, 10 s)`; a response that ends early with fewer bytes is a failure, not a rate |

Every scenario run starts a fresh host process and warms it up unmeasured. Tunnel payloads are 1 KB,
never a few bytes: ESET buffers loopback HTTP and stalls tiny payloads with or without Piper.

`rtt50_h1` and `rtt50_h2` are not a protocol-only comparison: the HTTP/2 run is HTTPS, so the proxy also
decrypts it. `tls_handshake_30` leaves the client's own certificate check out of the first request
(`harness_chain_p50_ms`). `h2_c100` makes the proxy open one upstream connection per request (a known gap),
and each closed one holds a loopback port in TIME_WAIT for about two minutes, so before a run it waits (up to
150 s) until at most 2,500 such sockets to the origin remain, and fails with that reason instead of recording
a run the exhausted ports ruined.

## Options worth knowing

`--wait-quiet PCT` waits, up to 60 s before each run, for the system CPU to fall under `PCT`; it takes a whole
number from 1 to 100 (decimals are refused), and the background CPU of each run is recorded either way.
`--duration S` is whole seconds too: the time `get_*` and `hdr_*` measure, the limit of `h2_c100` (it also ends at
its 2,000 requests), and the floor of the 10 s window `rtt50_*` read for.

## Measuring an older build

`--host label=path` takes any piper-bench build, and the host program uses only public Piper.Core API, so an older
release is measured by building this tool inside its checkout, which gives a proxy host with that checkout's
Piper.Core while the driver and origin stay the current ones (this is how v0.9.0 was measured against main):

```powershell
git worktree add ..\piper-v0.9.0 v0.9.0
robocopy tools\Piper.Bench ..\piper-v0.9.0\tools\Piper.Bench /E /XD bin obj   # exit codes 0 to 7 are success
dotnet build ..\piper-v0.9.0\tools\Piper.Bench\Piper.Bench.csproj -c Release
dotnet tools/Piper.Bench/bin/Release/net10.0/piper-bench.dll --runs 15 --out v0.9.0-vs-main.jsonl `
  --host before=..\piper-v0.9.0\tools\Piper.Bench\bin\Release\net10.0\piper-bench.dll --host after=self
```

Hosts alternate A,B,B,A, so a drift over the session does not favour one build. If the older checkout's public API
no longer matches `HostMode.cs` the copy does not build: adapt the copy, not this tree.

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

`cpu_per_req_us` and `alloc_per_req_bytes` are the growth of the proxy host's CPU time and allocated bytes
over the measured window, divided by the requests counted in it. The load keeps running while the host
answers a `stats` command, so the driver starts and stops counting just before it writes each command and
the host reads its counters a moment later (its wake-up time). Each edge errs by that time, in opposite
directions (a few requests too many in the divisor at the start, too few at the end), where earlier results
also counted the host's whole stats round trip at both edges against requests that were not counted. On the
machine this was measured on that moved `alloc_per_req_bytes` by about 0.5 to 0.7% (lower) for `get_c16`
and `cpu_per_req_us` by less than its run-to-run noise. Re-record both builds before comparing these two
metrics with a results file made before the change.

A request is counted (in `requests`, `errors`, `timeouts`, the latencies and the `maxRequests` budget) by when
it starts: the ones already in flight when the window opens belong to the ramp, and the ones that start inside
it are counted even if they finish after it closes. For a time-limited scenario the difference from counting by
completion is about `concurrency` requests out of thousands. `h2_c100` ends when its 2,000 requests are done and
has no ramp, so its first 100 (cold) requests start before the window opens: their work now falls inside the
window without being counted, and its `rps` reads up to 100/2,000 = 5% lower than in results recorded before this
change. Re-record the baseline before comparing `h2_c100` `rps` across it.

## Results files

`--out` names a new file (default `piper-bench-<UTC time>.jsonl`); an existing file is never overwritten, and a
device name or unwritable folder is refused before any host starts. Schema 2 is made to be shared: an `env`
line, a `build` line (configuration, runtime, GC, CPU, power plan, commit of the driver's checkout), then one
line per run, with no user, machine or folder name and no path (hosts by label, file name and the build configuration each
reports, warned about when it is Debug, at start and in `--compare`; files from before that read as unknown; paths removed
from errors, only known noisy processes named). `--host label=path` **runs** that file with your rights: name
only builds you made or trust.
