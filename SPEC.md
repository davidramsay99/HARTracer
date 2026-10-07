# HarLens: Local HAR Viewer and Request Composer

Specification v1.0, 2026-10-07. Working title "HarLens"; rename freely.

This document is the build brief for Claude Code. Sections 1 to 4 are binding constraints. Sections 5 to 9 define features. Section 12 defines build order and acceptance tests.

---

## 1. Purpose

A Windows desktop application for reading HAR (HTTP Archive) trace files, in the manner of Fiddler Classic's session list and inspectors, plus a request composer that imports, edits, sends, and exports cURL commands.

Primary user: a network support engineer who receives HAR files from customers and must find the failing request, read its headers and body, and reproduce it against a chosen endpoint.

## 2. Scope

| In scope | Out of scope |
| :- | :- |
| Open, inspect, filter, search, compare HAR 1.1 and 1.2 files | Live traffic capture, intercepting proxy, TLS (Transport Layer Security) decryption |
| Sanitized HAR export | Browser extension or DevTools integration |
| Request composer with cURL import and export | Scripting engine (FiddlerScript equivalent) |
| Replay of a captured entry, with diff against the original | Cloud sync, accounts, sharing links |
| Fiddler SAZ (Session Archive Zip) import, phase 4 | Auto-update, telemetry, crash upload |

## 3. Hard constraint: local only

These rules override every other section.

1. **The application initiates zero network traffic on its own.** No telemetry, update check, crash reporting, license check, remote fonts, remote icons, CDN (content delivery network) assets, or online help.
2. **The only code path that may open a socket is the Request Composer**, and only when the user presses Send, and only to the host the user has typed.
3. **Offline Mode** is a setting, default ON at first run. While ON, the Send button is disabled and the network assembly is never loaded. The user must turn it OFF deliberately to send requests.
4. **No embedded browser.** WebView2, CefSharp, and any HTML rendering engine are forbidden. HTML response bodies are shown as text only, since rendering would fetch subresources.
5. **The composer does not use the system proxy by default.** Proxy use is opt-in per request.
6. **All state stays on disk under `%LOCALAPPDATA%\HarLens\`.** No registry writes beyond optional file association, which the user triggers manually.
7. **The published application runs with no internet connection** and no runtime download. See section 11.

### 3.1 Enforcement

- `HarLens.Core` and `HarLens.App` must not reference `System.Net.Http`, `System.Net.Sockets`, `System.Net.WebSockets`, `System.Net.Dns`, or `System.Net.NetworkInformation` types. Enforce with `Microsoft.CodeAnalysis.BannedApiAnalyzers` and a `BannedSymbols.txt` in each project, with warnings promoted to errors.
- `HarLens.Net` is the single assembly permitted to use those APIs. `HarLens.App` loads it lazily, the first time Send is pressed with Offline Mode OFF.
- A test asserts that after opening a HAR, filtering, searching, and exporting, `HarLens.Net.dll` is absent from `AppDomain.CurrentDomain.GetAssemblies()`.

## 4. Platform and stack

| Item | Choice |
| :- | :- |
| OS | Windows 10 22H2 and Windows 11, x64 and arm64 |
| Runtime | .NET 10 (LTS, long-term support), target `net10.0-windows` |
| UI | WPF (Windows Presentation Foundation), MVVM (Model-View-ViewModel) |
| Language | C# with nullable reference types enabled, warnings as errors |
| JSON | `System.Text.Json`, using `Utf8JsonReader` for streaming |
| HTTP | `System.Net.Http.SocketsHttpHandler`, in `HarLens.Net` only |
| Tests | xUnit |

### 4.1 Permitted third-party packages

Keep this list closed. Adding a package requires a written justification in `DECISIONS.md`.

| Package | Use | License |
| :- | :- | :- |
| `CommunityToolkit.Mvvm` | MVVM source generators | MIT |
| `AvalonEdit` | Syntax-highlighted, virtualized text view | MIT |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | Build-time enforcement of section 3.1 | MIT |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | Tests | Apache-2.0 / MIT |

No package may ship with telemetry. Brotli, gzip, deflate, and zlib decoding come from `System.IO.Compression`. Zstandard bodies are displayed as undecoded bytes with a notice.

### 4.2 Solution layout

```
HarLens.sln
src/
  HarLens.Core/      # HAR model, streaming parser, filter engine, search,
                     # sanitizer, cURL parser and generators. No UI, no network.
  HarLens.Net/       # Request engine. The only project using System.Net.*.
  HarLens.App/       # WPF shell, views, view models.
tests/
  HarLens.Core.Tests/
  HarLens.Net.Tests/ # Uses an in-process loopback listener only.
  fixtures/          # Synthetic HAR files, see section 12.3.
SPEC.md
DECISIONS.md
```

## 5. HAR ingestion

### 5.1 Format support

- HAR 1.2 and 1.1. A missing or unknown `log.version` loads with a warning.
- Preserve every unknown field, including underscore-prefixed vendor fields, so that re-export loses nothing.
- Recognize and surface these vendor fields when present: `_initiator`, `_priority`, `_resourceType`, `_fromCache`, `_transferSize`, `_error`, `_webSocketMessages` (Chromium); `_securityState` (Firefox).
- Accept `.har`, `.json`, and gzip-compressed `.har.gz`.
- Accept a UTF-8 byte-order mark.

### 5.2 Semantics to honor

- `timings` values of `-1` mean "not applicable" and are excluded from sums.
- `timings.ssl` is included within `timings.connect`. Do not double count.
- `bodySize` and `headersSize` of `-1` mean "unknown".
- `content.encoding == "base64"` requires decoding before display.
- `content.size` is the decoded length; `bodySize` is bytes on the wire. `content.compression` is the difference when present.
- Entries are sorted by `startedDateTime` on load. The original file order is retained as a hidden index column.

### 5.3 Large files

| Requirement | Target |
| :- | :- |
| 100 MB HAR, time to first row displayed | under 3 s on an SSD |
| 1 GB HAR | opens; peak working set under 1.5 GB |
| Entry count | 200,000 rows scroll without stutter |

Method: one streaming pass with `Utf8JsonReader` over a memory-mapped file builds a lightweight index (one struct per entry holding the list columns, plus byte offsets of `request.postData.text` and `response.content.text`). Bodies are read from the file by offset on demand and held in a bounded LRU (least recently used) cache. The session list uses UI virtualization with recycling.

### 5.4 Malformed input

- A truncated file loads every complete entry and reports the count recovered and the byte offset of the failure.
- A parse error names the line, column, and JSON path.
- A file that is valid JSON but lacks `log.entries` produces a clear message and no crash.

### 5.5 Multiple files

- Each HAR opens in its own tab.
- Drag and drop onto the window opens files.
- "Merge into new session" combines selected tabs into one, tagging each entry with its source file.
- Recent files list, stored locally, clearable.

## 6. Viewer

### 6.1 Layout

Fiddler Classic arrangement: session list on the left, inspector on the right split into request (top) and response (bottom). A horizontal layout toggle places the inspector beneath the list. Splitter positions persist.

### 6.2 Session list

Default columns: `#`, Status, Method, Protocol, Host, Path, MIME type, Response size, Total time, Started (relative to first entry), Waterfall.

- Columns are sortable, reorderable, hideable. Layout persists.
- **Custom columns**: the user adds a column bound to any request or response header by name (example: `x-azure-ref`, `x-cache`, `x-ms-request-id`), or to a vendor field.
- Row coloring by status class: 2xx default, 3xx muted, 4xx amber, 5xx red, failed or blocked (`status == 0` or `_error` present) red italic.
- Waterfall column draws stacked bars for blocked, DNS, connect, TLS, send, wait, receive, on a shared time axis, with a tooltip listing each phase in milliseconds.
- Page grouping: when `log.pages` exists, an optional group header per page showing `onContentLoad` and `onLoad`.
- Multi-select, with a status bar showing count, total bytes, and time span of the selection.
- User annotations per entry: a comment and a color mark, written to the HAR `comment` field and `_harlens` vendor object on save.

### 6.3 Inspector tabs

Request side and response side each offer:

| Tab | Content |
| :- | :- |
| Headers | Name and value grid, original order, copy name, value, or both. General block above: URL, method, status, remote address (`serverIPAddress`), HTTP version, `connection` ID |
| Query | Decoded query string parameters as a grid (request only) |
| Cookies | Request cookies; response `Set-Cookie` with attributes parsed into columns |
| Body | Sub-views below |
| Raw | Reconstructed HTTP/1.1-style message text |
| Timing | Phase table and bar; explanation of each phase |

Body sub-views, auto-selected by MIME type and overridable:

- **Pretty**: JSON as a collapsible tree with a text toggle; XML and HTML indented; `application/x-www-form-urlencoded` and `multipart/form-data` as a parts grid.
- **Text**: syntax highlighted, word wrap toggle, line numbers.
- **Hex**: offset, hex, ASCII.
- **Image**: PNG, JPEG, GIF, BMP, ICO, WebP where the OS codec exists. SVG is shown as text only.
- **JWT (JSON Web Token)**: when a header or body value matches the three-part base64url pattern, offer a decoded header and payload view. Decoding only; no signature verification.

Additional inspector tabs for the entry as a whole: Initiator (`_initiator` stack, when present), WebSocket frames (`_webSocketMessages`), Cache (`cache.beforeRequest`, `cache.afterRequest`), and a "Raw HAR entry" JSON view.

### 6.4 Filtering

A filter bar above the list accepts free text and operators, combined with implicit AND. A leading `-` negates. The grammar follows Chrome DevTools network filter conventions.

```
status-code:403
status-code:5xx
method:POST
domain:*.contoso.com
scheme:https
mime-type:application/json
larger-than:100k
has-response-header:x-azure-ref
has-request-header:authorization
header:x-cache=TCP_MISS
is:from-cache
is:failed
time-greater-than:2000
/regex against url/
-mime-type:image/png
```

- Quick-toggle chips: All, XHR/Fetch, Doc, JS, CSS, Img, Font, Media, WS, Other; and 1xx to 5xx plus Failed.
- Saved filter presets, stored locally.
- The filter applies to the index only and must return within 100 ms for 200,000 entries.

### 6.5 Search

- `Ctrl+F`: find within the current inspector view.
- `Ctrl+Shift+F`: search across all entries, with scope checkboxes (URL, request headers, request body, response headers, response body), case sensitivity, and regex. Runs on a background thread with cancel and progress; results list jumps to the entry and highlights the match.
- Base64 bodies are decoded before searching when the MIME type is textual.

### 6.6 Compare

Select two entries and choose Compare. Show side-by-side diff of request line, headers (matched by name, case-insensitive), and bodies (line diff for text, pretty-printed first for JSON). Used also for original versus replay, section 7.5.

### 6.7 Statistics

A panel for the current selection or the whole session: request count, bytes sent and received, breakdown by status class, by MIME category, by host, slowest ten, largest ten, and a timeline histogram.

### 6.8 Copy and export

Per entry, via context menu:

- Copy URL, copy request headers, copy response headers, copy response body.
- Copy as cURL (bash), cURL (Windows cmd), PowerShell `Invoke-WebRequest`, raw HTTP.
- Save response body to file.
- Send to Composer.

Per session:

- Save HAR (preserving unknown fields and annotations).
- Export selected entries as a new HAR.
- Export list as CSV.
- **Export sanitized HAR**, section 8.

## 7. Request Composer

Disabled while Offline Mode is ON (section 3).

### 7.1 Editor

- Method dropdown with free entry, URL field, HTTP version selector (1.1, 2).
- Headers grid with per-row enable checkbox.
- Query parameter grid, two-way bound to the URL.
- Body editor with modes: none, raw text, JSON, form-urlencoded grid, multipart grid with file parts, binary from file.
- Auth helper: Basic, Bearer. These write the `Authorization` header and nothing else.
- Options: follow redirects (default OFF), maximum redirects, timeout, automatic decompression (default ON), skip certificate validation (default OFF, shows a red banner while ON), client certificate from PFX file or the Windows certificate store, proxy (default none).
- **Connect override**: send to a specific IP and port while preserving the URL host for the `Host` header and SNI (Server Name Indication). Equivalent to curl `--resolve` and `--connect-to`. Implemented with `SocketsHttpHandler.ConnectCallback`.
- `Content-Length` is computed automatically. The `Host` header is editable.

### 7.2 cURL import

Paste a command; the parser fills the editor. Must accept the three quoting dialects that browsers emit: bash (including `$'...'` ANSI-C quoting and backslash line continuation), Windows cmd (`^` escapes and continuation), and PowerShell (backtick continuation).

Supported options:

```
-X, --request            -H, --header             -d, --data, --data-ascii
--data-raw               --data-binary            --data-urlencode
--json                   -F, --form               -G, --get
-u, --user               -b, --cookie             -A, --user-agent
-e, --referer            -I, --head               -L, --location
--max-redirs             -k, --insecure           --compressed
-m, --max-time           --connect-timeout        -x, --proxy
--resolve                --connect-to             --http1.1, --http2
-E, --cert               --key                    --url
```

Options that affect only curl's output (`-s`, `-v`, `-i`, `-o`, `-w`, `-S`) are accepted and ignored. Any other option produces a visible warning naming it; the import still completes. `@file` references in `-d` and `-F` resolve against a user-chosen base directory and never silently.

### 7.3 cURL and code export

From the composer or any HAR entry, generate: cURL (bash), cURL (cmd), PowerShell `Invoke-WebRequest`, raw HTTP message. Round-trip rule: `parse(generate(request))` equals `request` for every fixture.

Default export omits the pseudo-headers (`:authority`, `:method`, `:path`, `:scheme`) and `Content-Length`.

### 7.4 Sending

- Engine: `HttpClient` over `SocketsHttpHandler`, one handler per send, `UseCookies = false`, `UseProxy = false` unless set, `AllowAutoRedirect = false` unless set.
- The exact bytes sent and received are recorded. Redirect hops, when followed, are recorded as separate entries.
- Timings captured: DNS, TCP connect, TLS handshake where measurable, time to first byte, download, total. Unmeasurable phases are written as `-1`.
- Also captured: remote IP and port, negotiated HTTP version, TLS protocol and cipher, server certificate subject, issuer, validity, and SAN (Subject Alternative Name) list.
- Cancel button aborts an in-flight request.
- Response size cap setting (default 100 MB), beyond which the body is truncated with a notice.

### 7.5 Results

- Each send appends an entry to a "Composer" session, viewable with the full inspector and savable as HAR 1.2 with `creator.name = "HarLens"`.
- When the request originated from a HAR entry, a "Diff against original" button opens the compare view of section 6.6.
- Composer history persists locally and is clearable. Saved requests can be grouped into named collections stored as JSON under `%LOCALAPPDATA%\HarLens\collections\`.

### 7.6 Replay guard

Captured requests carry live credentials. When a request sent from the composer originates from a HAR entry and still contains the captured `Authorization`, `Cookie`, or a known token query parameter, show a confirmation naming the target host and the credential-bearing fields, with a "strip credentials and send" alternative. A setting may suppress the prompt per host for the current run only.

## 8. Sanitization and secret handling

HAR files routinely contain session cookies, bearer tokens, and personal data.

- **Mask secrets in UI** toggle: replaces sensitive values with `••••` plus the last four characters, for screen sharing. Copy operations honor the mask.
- **Export sanitized HAR** dialog, with a preview listing every redaction before writing:
  - Headers: `Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, and any header matching a user list.
  - Query and form parameters: `access_token`, `id_token`, `refresh_token`, `code`, `client_secret`, `password`, `sig`, `signature`, `SAMLResponse`, `SAMLRequest`, and a user list.
  - Pattern rules in bodies: JWT, `Bearer <token>`, user-defined regexes.
  - Options: drop all response bodies, drop bodies by MIME type, hash values instead of removing them (stable salted SHA-256 so that equal values stay correlatable).
- The source file is never modified in place.
- Temporary files are not written. Bodies are decoded in memory.
- The application log records file paths and error text only; it never records header values or bodies.

## 9. Usability

- Keyboard: `Ctrl+O` open, `Ctrl+W` close tab, `Ctrl+F`, `Ctrl+Shift+F`, `Ctrl+L` focus filter, `Up`/`Down` move between entries, `Ctrl+C` copy URL, `Ctrl+Shift+C` copy as cURL, `Ctrl+R` send to composer, `Ctrl+Enter` send, `F6` cycle panes, `Esc` clear filter.
- Light and dark themes following the system setting, with an override.
- Monospace font for all protocol data, size adjustable with `Ctrl+Plus` and `Ctrl+Minus`.
- High-DPI aware (per-monitor v2). Full keyboard navigation and UI Automation names on interactive controls.
- Time display toggle: relative to first entry, local time, UTC.
- Size display toggle: bytes or human-readable.
- Command-line: `HarLens.exe <file.har> [--filter "<expr>"]`.

## 10. Non-functional requirements

| Area | Requirement |
| :- | :- |
| Startup | Cold start to interactive window under 1.5 s |
| Responsiveness | No UI-thread operation over 50 ms; parsing, search, and sends run off-thread with cancel |
| Stability | An unhandled exception shows a local dialog with a copyable stack trace and writes to `%LOCALAPPDATA%\HarLens\logs\`; nothing is transmitted |
| Privacy | Section 3 and section 8 |
| Settings | Single `settings.json`, human-readable, tolerant of unknown keys |
| Portability | A `portable.flag` file beside the executable redirects all state to a `data\` folder next to it |

## 11. Build and distribution

```powershell
dotnet restore --locked-mode
dotnet build -c Release -warnaserror
dotnet test  -c Release
dotnet publish src/HarLens.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

- Self-contained publish: the target machine needs no .NET install and no download.
- Do not enable trimming or Native AOT (ahead-of-time compilation); WPF does not support them.
- Commit `packages.lock.json`. Provide `scripts/vendor-packages.ps1` that fills a local `./nuget-offline/` feed so later builds restore with no internet.
- Build-time NuGet restore is the single point at which internet is used, and only on the build machine.
- No installer is required. Deliver a zip containing the executable, `LICENSE`, and `THIRD-PARTY-NOTICES.txt`.

## 12. Delivery plan

### 12.1 Phases

| Phase | Deliverable | Exit criteria |
| :- | :- | :- |
| 1 | `HarLens.Core`: model, streaming parser, index, filter engine, cURL parser and generators, sanitizer. Tests only, no UI | All Core tests pass; 1 GB fixture meets section 5.3 |
| 2 | Viewer: shell, session list, inspectors, filter bar, search, copy and export | Sections 5 and 6 acceptance checks pass manually and by test where automatable |
| 3 | Composer: `HarLens.Net`, editor, cURL import and export, replay, diff, replay guard, Offline Mode | Section 7 checks pass against the loopback test server; section 3.1 tests pass |
| 4 | Compare, statistics, sanitized export UI, annotations, collections, SAZ import, themes, portable mode | Remaining sections pass |

### 12.2 Acceptance tests

1. With the network adapter disabled, the application starts, opens every fixture, filters, searches, and exports. No error appears.
2. Section 3.1 assembly-load test passes.
3. The build fails if a banned network API is added to `HarLens.Core` or `HarLens.App`.
4. Open, save, reopen: the saved HAR is semantically identical to the source, vendor fields included.
5. Each Chrome "Copy as cURL (bash)", "Copy as cURL (cmd)", and Firefox "Copy as cURL" fixture imports to the expected request model.
6. cURL round-trip rule of section 7.3 holds for all fixtures.
7. A composer request with connect override reaches the loopback server on the override address, with the URL host in `Host` and SNI.
8. A request with redirect following OFF returns the 3xx response itself.
9. Sanitized export of the secrets fixture contains none of the planted secret strings (byte search of the output file).
10. The truncated fixture loads all complete entries and reports the failure offset.
11. Filter expressions in section 6.4 each return the expected entry IDs from the filter fixture.

### 12.3 Fixtures

Generate synthetic fixtures; do not use real captures. Required set: Chromium-style with vendor fields and WebSocket frames; Firefox-style; Safari-style; Fiddler-exported HAR 1.2; HAR 1.1; base64 binary bodies; gzip, deflate, and brotli-described content; entries with `-1` timings and sizes; `status: 0` failures; 200,000-entry file; 1 GB file (generated by script at test time, not committed); truncated file; valid JSON without `log`; secrets fixture with planted tokens.

### 12.4 Instructions to the implementer

- Work phase by phase. Do not begin a phase until the prior phase's exit criteria pass.
- Write tests alongside code in `HarLens.Core` and `HarLens.Net`.
- Record every deviation from this document, and every added dependency, in `DECISIONS.md` with the reason.
- When this document is silent, prefer the behavior of Chrome DevTools' Network panel for viewing and of curl for request semantics.
- Never add a network call outside `HarLens.Net`. If a feature seems to need one, stop and record the question in `DECISIONS.md`.

## 13. References

- HAR 1.2 specification, Jan Odvarko: http://www.softwareishard.com/blog/har-12-spec/
- W3C HAR draft (historical, unmaintained): https://w3c.github.io/web-performance/specs/HAR/Overview.html
- curl manual: https://curl.se/docs/manpage.html
- Chrome DevTools network reference (filter grammar, HAR export): https://developer.chrome.com/docs/devtools/network/reference
- `SocketsHttpHandler`: https://learn.microsoft.com/dotnet/api/system.net.http.socketshttphandler
- .NET support policy: https://dotnet.microsoft.com/platform/support/policy/dotnet-core
- Banned API analyzers: https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md
- Single-file deployment: https://learn.microsoft.com/dotnet/core/deploying/single-file/overview
