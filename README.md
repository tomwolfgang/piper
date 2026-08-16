# Piper

[![CI](https://github.com/tomwolfgang/piper/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/tomwolfgang/piper/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/tomwolfgang/piper?display_name=tag&sort=semver)](https://github.com/tomwolfgang/piper/releases)
[![License](https://img.shields.io/github/license/tomwolfgang/piper)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/windows/)

An HTTP(S) debugging proxy for Windows, written from scratch in C# on .NET 10 / WinForms.

## Why this exists

The **Composer** puts request search inside the editor: type a query, see matching captured
requests, load one, edit, and send.

The same query grammar drives the session-list filter and the composer search, so a query
you learn in one place works in the other.

## Layout

```
src/Piper.Core/          no UI dependencies, targets net10.0
  Http/                     header collection, buffered reader, parser, content codecs
  Http2/                    HPACK, framing, HTTP/2 client and server connections
  Http3/                    QUIC varints, HTTP/3 framing, QPACK, h3 client, Alt-Svc cache
  Proxy/                    proxy server, upstream connections, composer executor
  Security/                 root CA, per-host leaf certs, trust store management
  Sessions/                 session model, store, search query compiler
src/Piper.App/           WinForms shell, targets net10.0-windows
  Controls/                 session grid, inspectors, composer, autoresponder
  Theme/                    dark palette and owner-drawn list/tab controls
tests/Piper.SmokeTests/  end-to-end tests, no test framework needed
tools/Piper.TrafficGen/  local origin + traffic generator for manual UI testing
```

## Running it

```bash
dotnet run --project src/Piper.App/Piper.App.csproj
```

It listens on `127.0.0.1:8888` and starts capturing when the window opens.

To route the machine's traffic through it, use **Capture > Use as system proxy**. Your
existing proxy settings are saved and restored when you turn it off or close the app.

### HTTPS

Decrypting HTTPS needs the generated root CA trusted, via **Tools > Trust root
certificate**. Read the dialog before accepting: the private key sits unencrypted in
`%LOCALAPPDATA%\Piper\Certificates`, so anything able to read it can impersonate any
TLS site to your Windows account. **Tools > Remove trusted root certificate** reverses it.

Nothing installs the root implicitly — it is only ever a deliberate menu action.

## Search grammar

Terms are ANDed. Prefix any term with `-` or `!` to negate it.

| Form | Example | Matches |
| --- | --- | --- |
| bare word | `checkout` | URL, headers, or text bodies |
| quoted | `"order id"` | literal phrase |
| regex | `/orders\/[0-9]+/` | regular expression |
| `method:` `m:` | `method:GET\|POST` | alternatives with `\|` |
| `host:` `h:` | `host:api.example.com` | |
| `path:` `query:` `url:` | `path:/v2/users` | |
| `status:` `s:` | `status:404`, `status:4xx`, `status:>=400`, `status:200..299` | |
| `ct:` | `ct:json` | content type |
| `header:` | `header:Authorization`, `header:Accept=json` | request or response |
| `reqheader:` `respheader:` | | one side only |
| `body:` `req:` `resp:` | `body:user_id` | text bodies |
| `size:` `reqsize:` | `size:>100kb` | `b`/`kb`/`mb`/`gb` suffixes |
| `dur:` | `dur:>500` | milliseconds |
| `is:` | `is:json`, `is:error`, `is:composed`, `is:slow` | see Help > Search syntax |

Unknown fields are reported as a warning and ignored rather than failing the whole query.

```
method:POST host:api status:>=400 -is:image body:"order"
```

## Shortcuts

| Key | Action |
| --- | --- |
| `F12` | start / stop capturing |
| `Ctrl+K` | jump to the Composer search |
| `Ctrl+E` | send the selected session to the Composer |
| `Ctrl+S` | save selected sessions as a Fiddler SAZ archive |
| `Ctrl+X` | clear sessions |
| `Ctrl+C` | copy selected URLs |
| `Del` | remove selected sessions |
| middle-click | send a session to the Composer |

## Testing

```bash
dotnet run --project tests/Piper.SmokeTests/Piper.SmokeTests.csproj
```

Stands up real origin servers, runs the real proxy, and drives a real `HttpClient` through it —
320+ assertions covering proxying, chunked de-framing, gzip decode, failure capture, the composer,
header semantics, the full search grammar, HTTP/2 (HPACK against RFC 7541's own worked vectors,
framing, padding, flow control, multiplexing, and the real MITM path negotiating h2 on either or
both legs) and HTTP/3 (QUIC varints and QPACK against the RFC 9000/9204 worked vectors, plus a
real QUIC listener on loopback). Exit code is 0 on success. No test framework, so it runs without
a NuGet restore.

To exercise the UI by hand, start the app and run:

```bash
dotnet run --project tools/Piper.TrafficGen/Piper.TrafficGen.csproj -- 8888 19200
```

That serves a spread of status codes, content types and body shapes on `127.0.0.1:19200`
and sends them through the proxy. It must be a .NET process rather than Windows
PowerShell: .NET Framework's `WebProxy` unconditionally bypasses the proxy for loopback
targets, so `Invoke-WebRequest -Proxy` would never reach Piper.

## What works

- HTTP/1.1 forward proxying with keep-alive and connection reuse
- `CONNECT` blind tunnelling, and TLS termination with per-host certificates honouring SNI
- HTTP/2 on both legs (from-scratch HPACK, framing and flow control) — ALPN-negotiated with the
  browser inside a decrypted tunnel and, independently, with the origin server, so Piper freely
  translates between h1.1 and h2 on either side and records which protocol each leg actually used
- HTTP/3 to origin servers (from-scratch QPACK and framing over `System.Net.Quic`), off by
  default — see below
- Chunked de-framing; gzip, deflate and brotli decoding for display
- WebSocket / `101 Switching Protocols` upgrade pass-through
- Virtual-mode session grid that stays responsive under load
- Request and response inspectors: headers, decoded body, pretty-printed JSON, hex dump
- Composer with search, raw-request editing, repeat-N, and verbatim header sending
- Copy as curl, per-host filtering, dark theme
- Importing and exporting Fiddler SAZ session archives
- AutoResponder: ordered rules that answer a request locally instead of sending it upstream —
  see below

### AutoResponder

The **AutoResponder** tab holds an ordered rule list. Rules are checked top down and the first
enabled one that matches decides the answer, so a request can be served from a file, given a status,
delayed, redirected or dropped without ever reaching its origin. The syntax is Fiddler's, so rules
copied from a Fiddler setup work unchanged.

| Match | |
|---|---|
| `orders` | part of the URL, ignoring case |
| `EXACT:https://host/path` | the whole URL, case-sensitive |
| `NOT:orders` | everything the rest does not match |
| `REGEX:/v(?<n>\d+)/items` | a regular expression; `${n}` is then usable in the action |
| `METHOD:POST` | the request method |
| `HEADER:X-Env=staging` | a request header |
| `URLWithBody:coupon` | the URL and request body together |
| `Q:method:POST host:api` | Piper's own [search grammar](#search-grammar), request fields only |

| Action | |
|---|---|
| `*404`, `*503` | answer with that status |
| `C:\mocks\orders.json` | serve that file, content type from its extension |
| `*inline` | serve the rule's own body |
| `*raw:C:\path\captured.txt` | serve a complete saved response, headers included |
| `*redir:https://other/path` | send the client a 307 |
| `https://other/path` | fetch that instead, without telling the client |
| `*delay:500` | pause, then carry on — combine as `*delay:500 *503` |
| `*drop`, `*reset` | kill the connection |
| `*CORSPreflightAllow` | answer an `OPTIONS` preflight permissively |

Drag a captured session onto the tab (or use **Create AutoResponder rule** in the grid's context
menu) to build a rule that replays exactly what came back. Right-click a rule to edit its response as
raw HTTP, or as an editable JSON tree when the body is JSON. The **Test URL** box says which rule
wins for a URL and what it would return, without issuing a request, and each rule shows how many
times it has fired.

Sessions a rule answered are coloured differently in the grid, and searchable with `is:auto`.

Rules cannot see inside an undecrypted `CONNECT` tunnel, so a rule for an HTTPS host only fires when
that host is being decrypted.

### HTTP/3

Off by default. Turn it on with **Capture > Attempt HTTP/3 (origin, QUIC)**.

It is deliberately **upstream-only**. A browser pointed at a system HTTP proxy always tunnels
through `CONNECT` over TCP and disables QUIC for proxied traffic, so there is no such thing as a
browser speaking HTTP/3 *to* a forward proxy. What this does is let Piper dial the origin over
QUIC to see what it actually serves there.

An origin is only tried over QUIC after it has advertised `h3` in an `Alt-Svc` header on an
ordinary TCP response — never on the first, cold request, which is the one you are waiting on.
Failures are remembered per host with a cool-down, so a network that blocks outbound UDP/443
(many do) costs one timeout rather than one per request. Any failure falls back to TCP, and only
safe methods (`GET`, `HEAD`, `OPTIONS`) are attempted, so a fallback can never re-submit a request
with side effects.

QUIC comes from `System.Net.Quic` — msquic ships inside the .NET runtime, so this still needs no
NuGet packages and nothing extra installed. The HTTP/3 layer above it (framing, QPACK) is
from-scratch like the rest. QPACK uses the static table only and advertises a zero-capacity
dynamic table, which RFC 9204 explicitly permits and which removes the encoder/decoder instruction
streams entirely.

## Not implemented

- HTTP/2 or HTTP/3 in the Composer (raw/verbatim sending stays HTTP/1.1-only — the mandatory
  pseudo-headers and forbidden headers are structurally at odds with "what you type is what goes
  on the wire")
- HTTP/3 stream reuse (one QUIC connection per request) and server push
- Breakpoints, and tampering with a response the origin actually sent (the AutoResponder replaces
  responses, it does not edit real ones on their way back)
- Upstream proxy chaining
- zstd content decoding (bodies are shown as-is, not corrupted)

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for the automated checks and pull-request requirements.
Report suspected vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

Piper is licensed under the GNU General Public License v3.0 only. See [LICENSE](LICENSE).

## Releasing

Set the `<Version>` in `src/Piper.App/Piper.App.csproj`, commit it, then create and push a
matching tag (for example, `v0.2.0`). The GitHub release workflow verifies the tag matches the
project version, runs the smoke tests, and publishes an NSIS installer, a portable Windows x64
ZIP, a source ZIP, and SHA-256 checksums.

To build an installer from Visual Studio, open `Piper.slnx`, choose the `Release` configuration,
then right-click `installer/Piper.Installer` and select **Build**. The installer is written to
`installer/bin/Release/Piper-<version>-setup.exe`. Install NSIS first; if it is installed somewhere
other than its default location, set the `NsisExecutable` MSBuild property to its `makensis.exe` path.
The Modern UI installer lets people choose a current-user installation or an all-users installation;
the all-users option requests administrator approval and installs to Program Files.
Its displayed and registered version is read from the published `Piper.exe` file metadata.
The Components page also offers an optional desktop shortcut.

To build the installer locally, publish the app first and pass the generated paths to NSIS:

```powershell
$version = "0.1.0"
dotnet publish src/Piper.App/Piper.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o publish
New-Item artifacts -ItemType Directory -Force
$output = Join-Path (Resolve-Path artifacts).Path "Piper-$version-setup.exe"
& "${env:ProgramFiles(x86)}\NSIS\makensis.exe" `
  "/DPRODUCT_VERSION=$version" `
  "/DPUBLISH_DIR=$((Resolve-Path publish).Path)" `
  "/DOUTPUT_FILE=$output" `
  installer/Piper.nsi
```
