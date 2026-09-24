# Piper - Free Fiddler Classic Alternative for Windows

[![CI](https://github.com/tomwolfgang/piper/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/tomwolfgang/piper/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/tomwolfgang/piper?display_name=tag&sort=semver)](https://github.com/tomwolfgang/piper/releases)
[![License](https://img.shields.io/github/license/tomwolfgang/piper)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Windows](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](https://www.microsoft.com/windows/)

Piper is a free, open-source HTTP(S) debugging proxy for Windows and a modern alternative to
Fiddler Classic. It lets developers capture, inspect, filter, replay, compose, and mock
HTTP/HTTPS traffic, including TLS decryption, HTTP/1.1, HTTP/2, and upstream HTTP/3.

Migrating from Fiddler Classic? Piper can import and export Fiddler SAZ archives (including the
request-only `.raz` variant some Fiddler builds produce), and its AutoResponder supports
Fiddler-compatible rule syntax.

## Why Piper instead of Fiddler Classic?

Piper is a free, open-source Windows HTTPS debugging proxy with no paid edition, license key, or
feature tiers. It is designed for developers who want a modern Fiddler Classic alternative while
keeping familiar workflows and file formats.

| Capability | Piper |
| --- | --- |
| Platform | Windows desktop app |
| License | Open source (GPL-3.0-only) |
| HTTPS debugging proxy | Capture, inspect, filter, replay, compose, and mock HTTP/HTTPS traffic |
| Fiddler Classic migration | Import and export Fiddler SAZ archives (`.saz` full sessions, `.raz` request-only); Fiddler-compatible AutoResponder rules |
| Protocols | HTTP/1.1, HTTP/2, and upstream HTTP/3 |
| Developer tools | Composer, AutoResponder, search, and Copy as curl |

## Why this exists

The **Composer** puts request search inside the editor: type a query, see matching captured
requests, load one, edit, and send. Everything you have sent is grouped under its host in a
collapsible tree, with repeat sends of one endpoint folded into a single row and a count, and the
response to the last send is shown in the Composer itself rather than only in the capture grid.
To start from something you captured, drag it in from the grid and drop it anywhere on the
Composer -- or onto its tab, which works from whichever tab you are on.

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

Nothing installs the root implicitly - it is only ever a deliberate menu action.

Piper validates each origin server's certificate by default, the same as a browser would. If you
need to reach a known origin whose certificate doesn't validate (self-signed, expired, or a
hostname mismatch - e.g. hitting a raw load-balancer hostname that only its production domain's
certificate covers), **Configurations > HTTPS > Verify origin server certificates** can be turned
off. Leaving it off removes Piper's ability to detect a real attacker impersonating an origin, so
turn it back on once you're done testing.

## Search grammar

**Find Sessions** (`Ctrl+F`, or **Tools > Find sessions**) searches without hiding anything: it
marks every match in a colour you pick - yellow, orange, red, green, blue, purple or gray - and
optionally selects them. `F3` steps to the next match, and **Clear find marks** on the session
right-click menu removes the marks. Choosing *No highlight* unmarks the sessions a find matches.
A find can be scoped to headers, bodies, one side of the exchange, or URLs only.

The **filter** box above the session list (`Ctrl+Shift+F`) uses the same grammar but hides every
session that does not match.

Terms are ANDed. Prefix any term with `-` or `!` to negate it.

| Form | Example | Matches |
| --- | --- | --- |
| bare word | `checkout` | URL, headers, or text bodies |
| quoted | `"order id"` | literal phrase |
| regex | `/orders\/[0-9]+/` | regular expression |
| `method:` `m:` | `method:GET\|POST` | alternatives with `\|` |
| `host:` `h:` | `host:api.example.com` | substring of the host |
| `domain:` `d:` | `domain:example.com` | that host and its subdomains, never a lookalike such as `evil-example.com`; a fragment such as `curseforge` or `api.` matches any host containing it |
| `path:` `query:` `url:` | `path:/v2/users` | |
| `status:` `s:` | `status:404`, `status:4xx`, `status:>=400`, `status:200..299`, `status:4xx\|5xx` | |
| `ct:` | `ct:json` | content type |
| `header:` | `header:Authorization`, `header:Accept=json` | request or response |
| `reqheader:` `respheader:` | | one side only |
| `body:` `req:` `resp:` | `body:user_id` | text bodies |
| `size:` `reqsize:` | `size:>100kb` | `b`/`kb`/`mb`/`gb` suffixes |
| `dur:` | `dur:>500` | milliseconds |
| `is:` | `is:json`, `is:error`, `is:composed`, `is:slow` | see Help > Search syntax |

An unrecognised field is searched literally, so a pasted URL works as typed. A malformed value on
a known field — `status:abc` — is reported as a warning and ignored rather than failing the query.

A bare word scans a cached per-session haystack that skips non-text bodies and caps each message at
64,000 characters. Use `body:`, `req:` or `resp:` to search a large body in full.

```
method:POST host:api status:>=400 -is:image body:"order"
```

## Shortcuts

| Key | Action |
| --- | --- |
| `F12` | start / stop capturing |
| `Ctrl+F` | open Find Sessions, marking matches (with the session list focused) |
| `F3` | select the next session matching the last find |
| `Ctrl+Shift+F` | focus the session filter box, hiding non-matches |
| `Ctrl+K` | jump to the Composer search |
| `Left` / `Right` | collapse / expand a Composer history group |
| `Ctrl+E` | send the selected session to the Composer |
| `Ctrl+T` | open the TextWizard |
| `Ctrl+S` | save selected sessions as a Fiddler SAZ archive |
| `Ctrl+X` | clear sessions (outside text boxes, where it cuts text as usual) |
| `Ctrl+C` | copy selected URLs |
| `Del` | remove selected sessions |
| double-click a session | show it in the Inspectors tab |
| middle-click a session | send it to the Composer |
| click a session list column header | sort by that column; click it again to reverse, or sort by `#` for capture order |
| **Follow new sessions** button | appears over the session list whenever the newest row is not being followed (scrolled up, or sorted); click it to return to capture order and keep the newest session in view |
| drag a session | drop it anywhere on the Composer or AutoResponder, or onto its tab |
| `Ctrl+MouseWheel` | resize the UI font |
| `Ctrl++` / `Ctrl+-` | resize the UI font a step at a time |
| `Ctrl+0` | reset the UI font to 100% |

**View > Zoom** does the same from the menu. The size is clamped to 70-150% - past that a window
built from fixed row heights starts clipping its own text - and it is remembered between runs. The
status bar shows the current percentage whenever it is not 100%.

If Ctrl+MouseWheel gets in the way while scrolling, turn it off under
**Tools > Configurations > General**. The menu and the keyboard shortcuts keep working, so the size
is still reachable.

## Testing

```bash
dotnet run --project tests/Piper.SmokeTests/Piper.SmokeTests.csproj
```

Stands up real origin servers, runs the real proxy, and drives a real `HttpClient` through it -
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
- HTTP/2 on both legs (from-scratch HPACK, framing and flow control) - ALPN-negotiated with the
  browser inside a decrypted tunnel and, independently, with the origin server, so Piper freely
  translates between h1.1 and h2 on either side and records which protocol each leg actually used
- HTTP/3 to origin servers (from-scratch QPACK and framing over `System.Net.Quic`), off by
  default - see below
- Response bodies relayed as they arrive rather than buffered whole, so a download starts at
  once, a stream that never ends can be captured, and size is not a limit - see below
- Chunked de-framing; gzip, deflate and brotli decoding for display
- WebSocket / `101 Switching Protocols` upgrade pass-through, relayed in both directions until
  both sides close rather than until the first one does
- Virtual-mode session grid that stays responsive under load
- Request and response inspectors: headers, decoded body, pretty-printed JSON, hex dump
- Composer with search, raw-request editing, repeat-N, and verbatim header sending
- Composer history grouped into collapsible hosts, repeat sends folded into one counted row, and
  the response to the last send inspectable without leaving the Composer. Which hosts you have
  collapsed is remembered across restarts; a host you have not seen before starts expanded
- Copy as curl, per-host filtering, dark theme
- Importing and exporting Fiddler SAZ session archives, by drag-and-drop or **File > Open SAZ
  capture...**; a request-only `.raz` capture is appended to the Composer's history (no responses
  to inspect, but readily reloaded and resent) rather than the main request list. That history
  keeps the 2,000 most recent requests and drops the oldest beyond that
- AutoResponder: ordered rules that answer a request locally instead of sending it upstream -
  see below
- TextWizard: encode, decode and hash a value without leaving Piper — see below

### TextWizard

**Tools > TextWizard** (`Ctrl+T`) opens a scratchpad that converts one piece of text at a time, laid out
like Fiddler's: input on top, the transform dropdown between the two panes, output below. The output
updates as you type and the title tracks the character counts.

The transform list matches Fiddler's, in the same order:

| | |
| --- | --- |
| Base64 | To Base64, To Base64URL, From Base64 |
| URL | URLEncode, URLDecode — decoding treats `+` as a space, the way a query string does |
| Hex | HexEncode, HexDecode — uppercase and unspaced |
| Code | To C# byte[], To JS string, From JS string |
| HTML | HTML Encode, HTML Decode |
| UTF-7 | To UTF-7, From UTF-7 — for legacy gateways and UTF-7 filter-evasion payloads |
| SAML | To DeflatedSAML, From DeflatedSAML — the raw-DEFLATE HTTP-Redirect binding |
| Hashes | To MD5, SHA1, SHA256, SHA384, SHA512, as uppercase hex |

When it opens with a value sent from an inspector it guesses the encoding and preselects the matching
decoder - base64, URL, hex, HTML entities, a JSON string literal, UTF-7 or a deflated SAML payload - and
says so on the status bar. The guess is only a hint; picking something else is always one click away. When
nothing is recognisable it falls back to the last transform you chose yourself, which is remembered between
runs. Only the name of the transform is stored, never the text.

**View bytes** shows the output as a hex dump, **Save** writes the output to a file, and **To Input** feeds
it back round for a second pass. The window is resizable from the grip on its status bar.

Rather than opening it and pasting, you can send a value straight from a capture: **Send value to
TextWizard** sits on the Headers, JSON and WebForms inspector context menus, and **Send URL to TextWizard**
on the session grid. The window is shared and stays open beside the grid.

Text is treated as UTF-8 throughout; bytes that are not valid UTF-8 come back as `�`, so use the Hex
inspector for genuinely binary payloads. **From Base64** is deliberately forgiving — either alphabet,
padding optional, line wrapping ignored — because that is how base64 arrives in headers and JWTs; illegal
characters are still an error. Other decoders given malformed input say so instead of guessing. Input is
capped at 1 MiB, and `From DeflatedSAML` refuses to inflate past 1 MiB so a compression bomb cannot
exhaust memory.

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
| `*delay:500` | pause, then carry on - combine as `*delay:500 *503` |
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
ordinary TCP response - never on the first, cold request, which is the one you are waiting on.
Failures are remembered per host with a cool-down, so a network that blocks outbound UDP/443
(many do) costs one timeout rather than one per request. Any failure falls back to TCP, and only
safe methods (`GET`, `HEAD`, `OPTIONS`) are attempted, so a fallback can never re-submit a request
with side effects.

QUIC comes from `System.Net.Quic` - msquic ships inside the .NET runtime, so this still needs no
NuGet packages and nothing extra installed. The HTTP/3 layer above it (framing, QPACK) is
from-scratch like the rest. QPACK uses the static table only and advertises a zero-capacity
dynamic table, which RFC 9204 explicitly permits and which removes the encoder/decoder instruction
streams entirely.

## Large and long-lived responses

Piper forwards a response body to the client as it arrives from the origin, rather than reading the
whole message first. What the origin used to frame the body is what Piper sends: a `Content-Length`
is passed through unchanged, a chunked body stays chunked. Nothing is re-framed, because a client
that draws a progress bar from `Content-Length` has nothing to draw with if the length is dropped.

The exceptions are the cases where passing the framing on would be wrong. A `Content-Length` sent
beside chunked coding is dropped, since chunked wins and the length describes nothing that is
relayed. An HTTP/1.0 client, which has no chunked coding, gets a chunked body de-chunked and ended by
the connection closing. A response whose `Content-Length` is unreadable or contradicts itself is
refused with a 502 rather than relayed. If the origin fails once the body has started, the client's
connection is reset (an HTTP/2 stream gets `RST_STREAM`), so a cut-off download is never mistaken
for a finished one.

It holds whichever protocol either leg speaks. From the origin, an HTTP/1.1 body is relayed as it
is read and an HTTP/2 body DATA frame by DATA frame -- which matters, because a decrypted HTTPS
origin, a CDN in particular, usually negotiates HTTP/2. Towards the client, an HTTP/2 body is framed
into DATA frames as the bytes arrive, and a sender that exhausts the peer flow-control window
resumes on the grant that gives it more rather than on the next tick of a timer.

The one exception is an HTTP/3 origin, which is off by default (`EnableHttp3Upstream`): its
response is still read whole before any of it is forwarded, so none of the three points below holds
for it. What the capture keeps of such a body is bounded by `MaxCapturedBodyBytes` all the same.

This matters in three ways:

- **A download starts immediately.** Buffering meant the client saw nothing until the last byte had
  arrived, so a large file was indistinguishable from a hang, and a downloader with its own stall
  timeout would give up part way through a transfer that was working.
- **A response that never ends can be captured at all.** Server-sent events, long polling and live
  media never complete, so a proxy that waits for the end of the message waits for ever.
- **Size is not a limit.** There is no ceiling on what can pass through.

A body still arriving is shown as such in the grid. Its Result reads `↓ 200` instead of `200`, its
Time counts up, and its Size grows as the bytes come in. When the origin announced a
`Content-Length`, Size reads `3.0/8.0 MB` over a fill showing how far through the body is; without
one there is nothing to measure against, so there is no fill, only the count. The status bar and
the inspector give the long form, `3.00 MB of 8.00 MB (37%)`, and `is:inflight` finds these
sessions. The figures are read at the grid's refresh rate rather than reported per chunk, so a
download costs no more than repainting its row. A body that fails part way keeps the size that did
arrive.

So that Size and Time stay in view, the grid keeps every column on screen down to a list about
720 px wide at 100% scale, and only then scrolls sideways. Host, Path, Type and Process give up width
first, and a long Path is shortened in the middle so its file name stays visible wherever the name
itself fits. The compact columns
are sized to the widest figure they can show at the current display scale and zoom, so a progress
figure is never cut off.

What *is* bounded is how much of a body is kept for inspection, by two limits:

| | |
|---|---|
| `MaxCapturedBodyBytes` | how much of a single body is retained (default 32 MB) |
| `RetainedBodyBudgetBytes` | how many body bytes are kept across all sessions (default 512 MB) |

Past the first, the rest of the body is relayed but not kept. Past the second, the oldest sessions
give up their bodies -- the sessions themselves stay, with their URL, status, timings and size,
because what a capture is mostly used for is seeing that a request happened at all. A session count
is not a bound on memory: twenty thousand sessions is nothing if they are API calls and several
gigabytes if they are downloads.

Nothing reports a partly kept body as a small one. The grid and the inspector show the length that
crossed the wire, the inspector says how much of it was retained, and a `.saz` export marks a
partial body with `X-Piper-Body-Truncated` rather than writing the fragment as though it were the
whole thing. Bounding what is retained is what lets the relay itself be unconditional: a modpack
install fetching hundreds of files would otherwise spend its time collecting garbage instead of
proxying.

The trade this makes is that a body can no longer be edited on its way back to the client. Piper
has never offered that -- the AutoResponder replaces responses rather than editing real ones -- so
there is nothing to give up here, which is why there is no buffering mode to switch between.

## Not implemented

- HTTP/2 or HTTP/3 in the Composer (raw/verbatim sending stays HTTP/1.1-only - the mandatory
  pseudo-headers and forbidden headers are structurally at odds with "what you type is what goes
  on the wire")
- HTTP/3 stream reuse (one QUIC connection per request) and server push
- Relaying an HTTP/3 origin's response as it arrives (it is read whole first; see
  [Large and long-lived responses](#large-and-long-lived-responses))
- Breakpoints, and tampering with a response the origin actually sent (the AutoResponder replaces
  responses, it does not edit real ones on their way back)
- Upstream proxy chaining
- zstd content decoding (bodies are shown as-is, not corrupted)

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) for the automated checks and pull-request requirements.
Report suspected vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

Piper is licensed under the GNU General Public License v3.0 only. See [LICENSE](LICENSE).

## Updates

On startup, Piper checks the public GitHub latest-release endpoint and shows a prompt only when a
newer version is available. **Help > Check for updates...** runs the same check manually. Each
check is recorded as a composed `Piper (update check)` session. Update checks bypass all
session-list filters, so their request and response are always visible in the Sessions list.

Choosing to update downloads the release installer and its `SHA256SUMS.txt` manifest directly from
GitHub. Piper verifies the installer hash before starting it, then closes so the installer can
replace the running executable. Update checks send only the normal Piper user-agent and never send
captured traffic.

## Anonymous feedback

Piper can report anonymous usage and error information so its rough edges can be found and fixed.
**It is off unless you turn it on.** The first run asks once, with both answers given equal weight;
declining, closing the dialog, or pressing Escape all leave it off, and nothing is collected or
transmitted until you agree.

**Where it goes:** `analyticsnew.overwolf.com`, over HTTPS, run by Overwolf. "Anonymous" describes
the contents of the reports, not the connection: sending one is a web request like any other, so it
reveals your IP address and when you were using Piper.

**What is sent:** which features you use, how a capture or certificate step turned out, the type of
any error with its top few stack frames, Piper's version and your Windows version, and a random
identifier stored in `HKCU\Software\Piper` so repeat reports can be grouped. That identifier is
deleted when you turn reporting off, but it is not removed by uninstalling Piper, so a reinstall on
the same Windows account reports under the same identifier unless you turned reporting off first.

**What is never sent:** captured traffic, URLs, hostnames, headers, bodies, cookies, credentials,
certificates, private keys, file paths, exception messages, or your settings. Reporting can only
carry values made of letters, digits, `.`, `_`, and `-`, checked when an event is recorded and again
when it is read back from disk, so a URL or header cannot be reported even by a mistake in Piper's
own code. Counts are reported as buckets rather than exact figures.

Reports wait in `%LOCALAPPDATA%\Piper\analytics\pending.jsonl`, a plain text file you can read before
it is sent. **Tools > Configurations > Privacy** holds the switch and a button that opens that
folder. Turning reporting off deletes the identifiers and discards anything not yet sent.

## Reporting a problem

The **Log** tab records what Piper is doing to itself: startup state, refused file drops, SAZ
imports, capture and certificate decisions. **Help > Save diagnostics for a bug report...** writes
that log, a summary of the machine and the tail of the local crash log to a zip you choose.

The bundle contains no captured requests, responses, bodies, cookies or certificates, and Piper
never uploads it — you choose the destination and attach it to a report yourself.

It is Piper's own log, not a redacted one. Your Windows account name is replaced with
`%USERPROFILE%` and control characters are flattened so nothing can forge log lines, but a message
can still name a host you filtered, an AutoResponder rule you wrote or a capture file you opened.
Read it before sending it on.

If dropping a `.saz` file onto the window does nothing, the log usually names the reason:

- a modal prompt (the root-certificate question on first run) is open, which disables the window
- Piper is running elevated, so Windows blocks drags from a normal Explorer window
- the file came from mail, an archive viewer or a browser download shelf and has no path on disk
  yet — save it to a folder first

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
