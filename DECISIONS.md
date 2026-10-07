# HarLens: decisions and deviations

SPEC 12.4 asks that every deviation from SPEC.md, and every added dependency, be recorded here with its reason.
Entries are grouped by area. "Spec" cites the section concerned.

## Dependencies

No package outside the SPEC 4.1 list was added.

| Package | Version | Note |
| :- | :- | :- |
| CommunityToolkit.Mvvm | 8.4.2 | Partial-property source generators (C# 14). |
| AvalonEdit | 6.3.1.120 | Latest; targets net8.0-windows, runs on net10.0-windows. |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 4.14.0 | Not 5.x: a 5.x analyzer may require a newer Roslyn than the SDK 10.0.1xx compiler ships, which fails the build under warnings-as-errors. 4.14 supports namespace bans (`N:`). |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 2.9.3 / 3.1.5 / 18.10.1 | The spec names `xunit` (v2), so xunit.v3 was not used. |

Build-machine tools used only to produce committed files: Python 3 runs `scripts/fixtures/make_fixtures.py` and
`scripts/make-notices.py`. The brotli sample in the fixtures was produced once with `System.IO.Compression.BrotliStream`
because Python's standard library has no brotli encoder.

## Solution layout and build (spec 4, 11)

1. **HarLens.Core and HarLens.Net target `net10.0`, not `net10.0-windows`.** Neither uses Windows APIs, and the
   plain target lets their tests run on any OS (the suite was developed on Linux). HarLens.App targets
   `net10.0-windows` as specified and consumes both.
2. **Two extra test projects.**
   `tests/HarLens.Isolation.Tests` holds the SPEC 3.1 assembly-load test and acceptance test 3 (the build fails when
   a banned API is added). It must run in its own test host process so no other test loads HarLens.Net first.
   `tests/HarLens.App.Tests` is a Windows-only UI smoke test that creates the real windows on an STA thread, opens
   fixtures, exercises every body view, filters, themes and tool windows, and asserts HarLens.Net is still not loaded.
   On non-Windows systems it builds but is not marked as a test project.
3. **Banned API lists are wider than the spec minimum.** Core and App also ban `System.Net.Security`,
   `System.Net.Quic`, `System.Net.Mail`, `WebClient`, `WebRequest` and subclasses, `HttpListener`,
   `ServicePointManager` and `WebProxy`. App additionally bans the `HarLens.Net` namespace (it may only be reached
   through `RequestEngineLoader`), `WebBrowser`, `Frame`, `NavigationWindow` (SPEC 3.4), `BitmapImage.UriSource`
   (images load only from memory) and `System.Diagnostics.Process` (no browser or online help can be launched).
4. **How HarLens.Net stays unloaded.** HarLens.App keeps a project reference to HarLens.Net so the assembly ships
   inside the single-file exe, but never names its types. `HarLens.Core.Engine.RequestEngineLoader` loads it with
   `Assembly.Load` on the first Send with Offline Mode OFF.
5. **Stable lock files.** `Directory.Build.props` sets `RuntimeIdentifiers=win-x64;win-arm64` for every project and
   turns off the single-file, trim and AOT analyzers. Without this, `dotnet publish -r win-x64` rewrote
   `packages.lock.json` (adding RID sections and the SDK-version-specific `Microsoft.NET.ILLink.Tasks`), which would
   make `dotnet restore --locked-mode` fail on a machine with a different SDK patch. Trimming and Native AOT stay off
   (SPEC 11).
6. **Locked mode is explicit.** Restore uses `--locked-mode` as in SPEC 11. `restore --locked-mode -r <rid>` is not
   possible (the RID changes the graph), so `scripts/vendor-packages.*` collect the runtime packs with a throwaway
   publish instead. Offline restore and offline publish from `./nuget-offline` alone were verified into an empty
   package folder.
7. **Scripts.** `scripts/vendor-packages.ps1` (spec) plus a `.sh` twin; `scripts/package.ps1` builds, tests,
   publishes win-x64 and win-arm64 and writes `artifacts/HarLens-<version>-<rid>.zip` with `HarLens.exe`, `LICENSE`
   and `THIRD-PARTY-NOTICES.txt`.
8. **CI.** `.github/workflows/build.yml` (not in the spec) runs the SPEC 11 commands on `windows-latest`, the
   large-file checks, the UI smoke test, and uploads the zips. `actions/setup-dotnet` downloads the SDK on the runner;
   that is build-machine traffic, as SPEC 11 allows.
9. **LICENSE is a placeholder.** It reserves all rights. The project owner should choose the actual license.
10. **Fixtures are byte-exact.** `.gitattributes` marks `tests/fixtures/**` binary; a Windows checkout with
    `core.autocrlf` otherwise rewrites them to CRLF and shifts the byte offsets the truncation tests assert.
11. **Telemetry on the build machine.** The .NET CLI has its own telemetry. Scripts and CI set
    `DOTNET_CLI_TELEMETRY_OPTOUT=1`. The shipped application contains no telemetry.

## HAR ingestion (spec 5)

1. **Streaming over a buffered FileStream, not a memory-mapped file (spec 5.3).** Pages of a memory-mapped file count
   toward the process working set, which works against the 1.5 GB ceiling for a 1 GB file. The parser reads the
   file sequentially into a growable buffer, locates each entry with `Utf8JsonReader.TrySkip`, and indexes it from a
   contiguous span. Bodies are read later by positional `RandomAccess.Read` on a handle opened with read sharing only,
   so the file cannot change underneath the index. Measured on the 4-vCPU Linux container used for development
   (Release):

   | Fixture | Result | Target |
   | :- | :- | :- |
   | 98.5 MB, 26,000 entries | indexed in 1.1 to 1.2 s | first row under 3 s |
   | 1.43 GB, 100,000 entries | indexed in 6.7 s, peak working set 269 MB | opens, under 1.5 GB |
   | 329 MB, 200,000 entries | loaded and sorted in 3.4 to 3.8 s | scrolls without stutter (UI, see "Verification") |
   | 200,000 entries, eight filter expressions | slowest cold first pass 92 ms (`header:x-cache=TCP_MISS`), warm 8 to 54 ms | under 100 ms |

   The 100 ms filter margin is thin on this container for header filters, so the hot paths skip tiered JIT
   (`AggressiveOptimization`), and each file's header pairs are stored in contiguous 4,096-pair blocks with every
   entry holding a segment, so a header filter scans memory sequentially instead of chasing two small arrays per
   entry.

2. **The index row is a class (`HarEntry`), not a struct.** Rows are shared by reference between the list, filters,
   selection and annotations; a struct would be copied or boxed at each of those. Repeated strings (header names,
   short values, methods, MIME types, hosts) are interned per file to keep the index small.
3. **Gzip files are decompressed into memory** (chunked, no array over 2 GB). Temporary files are never written.
   A UTF-16 file with a BOM is transcoded to UTF-8 in memory. A UTF-8 BOM is skipped.
4. **Lenient JSON.** Comments and trailing commas are accepted; the HAR spec forbids neither reading them.
5. **Failure reporting (spec 5.4).** When parsing stops, the value being read is re-scanned to find the JSON path
   and the offset of the last good token. If the data is a valid JSON prefix that runs out at end of file, the failure
   is "truncated" and the reported offset is the file length. Lines and columns are 1-based; columns count bytes.
   All complete entries before the failure are kept. A syntax error before `log.entries` is reached is fatal and the
   message names line, column and path.
6. **Vendor fields.** Besides the spec list, Safari's `_fetchType` ("Memory Cache", "Disk Cache") marks an entry as
   from cache, and `_error` is read at entry, request or response level (Chromium writes it on the response).
7. **Sort order.** Entries sort by `startedDateTime`, ties by source file then original index. Missing or unreadable
   dates sort first.

## Saving and exporting (spec 6.8, 8)

1. **Unchanged entries are copied byte for byte** from the source, so unknown and vendor fields survive exactly
   (acceptance test 4 checks semantic equality for every fixture). Entries whose annotations changed, merged entries
   (which gain `_harlens.source`) and entries whose page id was renamed are rewritten through `JsonNode`, which keeps
   every other field.
2. **Annotations**: the comment is the HAR `comment` field; the color is `_harlens.color`.
3. **Save never overwrites an open source file.** The user is asked for another name. This also covers sanitized
   export (SPEC 8: the source file is never modified in place).
4. **Entry order on save**: original file order for a single-file session; start-time order for merged and composer
   sessions. Exporting a subset keeps only the pages its entries reference. When merged files share a page id, the
   later one is renamed `<id>_2` and its entries are remapped.
5. **CSV** cells that a spreadsheet would read as a formula (leading `= + - @`) get a leading apostrophe, because
   header values in customer HARs are untrusted. Numeric columns are left alone. UTF-8 with BOM, RFC 4180 quoting.

## Viewer (spec 6)

1. **Response size column** = `_transferSize` when present, else `bodySize + headersSize` when both are known, else
   `bodySize`, else `content.size`. `larger-than` and `smaller-than` use the same value; `k` = 1000 and `M` =
   1,000,000 as in Chrome DevTools, and the comparison is `>=` as in Chrome.
2. **Filter grammar additions** beyond the spec list, all from Chrome DevTools or useful for support work:
   `smaller-than`, `time-less-than`, `resource-type`, `url`, `priority`, `remote-address`, `set-cookie-name`,
   `set-cookie-value`, `set-cookie-domain`, `cookie-name`, `cookie-value`, `is:running`, `is:websocket`,
   `is:redirect`, `is:annotated`, `comment`, `color`, `source`. Unknown keys are treated as free text, as Chrome does.
3. **`header:` semantics** (not defined by Chrome): `header:name` matches when either side has the header;
   `header:name=value` matches when a value contains the text (case-insensitive), or matches a `*` wildcard pattern.
   This makes `header:x-cache=TCP_MISS` match Akamai's `TCP_MISS from a23-...`.
4. **Wildcards** (`domain:*.contoso.com`) are matched without regex, and the regex term is compiled, to keep
   200,000-entry filters under 100 ms. `domain:*.contoso.com` does not match `contoso.com` itself.
5. **Quick chips** combine with OR within the type group and within the status group, and with AND across groups and
   the filter text. Types come from Chromium's `_resourceType`, else `Sec-Fetch-Dest`, else the MIME type.
6. **Inspector arrangement.** The entry-level tabs sit with the side they relate to: Initiator, Raw HAR Entry and
   Notes on the request side; WebSocket, Cache, TLS and Replay on the response side. Timing appears on both sides.
7. **Pretty view.** JSON is a lazily expanded tree with a text toggle; HTML is indented by a tag-based indenter (no
   DOM, no rendering, SPEC 3.4); XML is indented with DTD processing ignored and no resolver, so nothing external is
   fetched. Text editors show at most 16 M characters of a body, with a notice; Save writes all of it.
8. **Images** decode through the Windows Imaging Component. WebP and HEIF depend on the codecs installed; when none
   exists the view says so. SVG is text only.
9. **AvalonEdit hyperlinks are disabled**, so clicking a URL in a body can never launch a browser. Syntax
   highlighting is on in the light theme only: AvalonEdit's built-in palettes are designed for light backgrounds and
   its definitions are frozen, so dark mode shows text in the theme's foreground color without highlighting.
10. **Search (Ctrl+Shift+F)** matches header lines as `Name: Value`, keeps at most 100 hits per location and 10,000
    overall, and can span all open tabs. Ctrl+F uses AvalonEdit's search panel in text views; header grids have a
    find box.
11. **Compare** uses Myers' line diff with common prefix and suffix trimmed. Beyond 2,000 differing lines it falls
    back to positional pairing to bound memory.
12. **Mask secrets** keeps the Authorization scheme and cookie names visible and masks the values, URL parameters,
    JWTs and Bearer/Basic tokens in text. Copies, cURL exports, the Raw tabs and search snippets honor it.
13. **Group by page** uses a grouped `ListCollectionView` only while the option is on; otherwise the list binds to a
    plain list, and sorting is done by the view model, which is what keeps 200,000 rows responsive.
14. **Themes** use WPF's Fluent theme (`Application.ThemeMode`), which follows the Windows light/dark setting and
    supports an override. The API is marked experimental (`WPF0001`, suppressed in the App project). HarLens' own
    colors (status rows, waterfall phases, diff backgrounds) swap with it. The registry is read
    (`AppsUseLightTheme`), never written.

## Sanitized export (spec 8)

1. **Replacement token** `[REDACTED]`; with hashing, `[sha256:<first 16 hex of HMAC-SHA256(salt, value)>]`. The salt is
   random per export unless set, so values correlate within one file but not across exports.
2. **Authorization keeps its scheme word and cookies keep their names** (`Bearer [REDACTED]`,
   `session=[REDACTED]; theme=[REDACTED]`), which keeps the file useful for troubleshooting.
3. **Beyond the spec's rules**, because the acceptance test is a byte search for planted secrets:
   parameter names are also applied to JSON properties in bodies (OAuth token responses); query and fragment
   parameters are redacted inside any string (Referer, Location, `redirectURL`, `_initiator` URLs, page titles);
   `Basic <base64>` credentials in text are redacted; textual base64 bodies are decoded, sanitized and re-encoded;
   and every value removed by a name rule is swept from the rest of the file (values of 8 characters or more).
4. **Drop by MIME type** applies to request and response bodies; a dropped body gets a `comment` saying so.
5. **Preview** lists each redaction with the original masked (`••••` plus the last four characters); the list is capped
   at 100,000 rows with the total shown.

## Composer and request engine (spec 7)

1. **One `SocketsHttpHandler` per hop**, not per send, and redirects are followed by the engine
   (`AllowAutoRedirect` is always false), so that every hop is recorded as its own entry with its own DNS, connect
   and TLS timings. curl -L rules: 301/302/303 switch POST to GET, 303 switches any non-HEAD to GET, 307/308 keep the
   method and body; Authorization and Cookie are dropped when scheme, host or port change. Statuses followed: 300,
   301, 302, 303, 307, 308. At MaxRedirects the last 3xx is recorded and the send reports an error.
2. **Decompression is done by the engine**, not `AutomaticDecompression`, so the recorded headers keep
   `Content-Encoding` and the wire size is known. With automatic decompression on and no Accept-Encoding header,
   `gzip, deflate, br` is offered (curl `--compressed`).
3. **Local-only TLS.** Revocation checks and certificate downloads (CRL, OCSP, AIA) are off, and the client
   certificate context is built offline, so a send connects only to the typed host, the override target or the
   explicit proxy. TLS session resumption is off so `ssl` timings are comparable.
4. **SNI follows a custom Host header.** SocketsHttpHandler derives SNI and the certificate name check from the Host
   header; curl uses the URL host. Connect overrides keep the URL host in both, as the spec requires.
5. **No implicit User-Agent or Accept**; only the composed headers are sent. JSON and form bodies get a default
   Content-Type when none was composed (curl `--json`, `-d`). Header values with CR, LF or NUL and invalid names are
   skipped with a notice. Duplicate Host or Content-Type: the first is used. A composed Content-Length is ignored.
6. **HTTP versions.** Default = HTTP/1.1 allowing ALPN upgrade to HTTP/2; `--http2` = 2.0 or lower. h2c (HTTP/2 over
   plain http) is not supported and falls back with a notice. HTTP/3 is never attempted.
7. **Wire capture.** The exact plaintext bytes each way are recorded (HTTP/1.1 text or HTTP/2 frames) up to 8 MB per
   direction and stored in `_harlens.wire` when under 4 MB. For HTTP/1.1, recorded headers are parsed from those bytes;
   for HTTP/2 they come from the message objects and include pseudo-headers. Trailers are appended to the response
   headers. The Raw tabs of a composer entry show the wire bytes.
8. **Proxy.** Only an explicit proxy is used, never the system proxy or WPAD. Connect overrides do not apply through
   a proxy (notice). Proxy credentials in the URL are sent after a 407 challenge.
9. **Errors** use curl's wording. A cancelled send sets both `Cancelled` and `Error = "Cancelled"`; checks that fail
   before connecting produce no exchange.
10. **Composer history stores no credential values by default.** Authorization, Proxy-Authorization and Cookie values
    and the client certificate password are blanked before the item is written (an option keeps them). Collections
    store requests as saved, because saving one is an explicit act.
11. **Replay guard** compares the request with the captured entry: Authorization, Proxy-Authorization and Cookie with
    their captured values, and token-like query parameters (the sanitizer's list plus `token`, `api_key`, `session`,
    `jwt`, `assertion`, `ticket` and similar). "Don't ask again" lasts for the current run only.

## cURL import and export (spec 7.2, 7.3)

1. **Round trip** is enforced for the three cURL dialects (bash, cmd, PowerShell `curl.exe`) over every fixture and
   about 600 generated adversarial requests. Invoke-WebRequest and raw HTTP are export-only, since the spec's parser
   imports cURL. A third cURL dialect, PowerShell `curl.exe`, was added so every import dialect has an export.
2. **Parser additions beyond the option list**: `--form-string`, `--oauth2-bearer`, `--cert-type`, `--pass`,
   `-g/--globoff`, `--variable` and `--expand-*`, `--no-<flag>`, `--next` (stops with a warning),
   `--http2-prior-knowledge` (imported as HTTP/2 with a warning). `--http1.0` and `--http3` warn.
3. **Faithful to curl's wire behaviour**, checked against curl 8.5.0 on loopback: multiple `-b` join with `;`;
   `--data-urlencode` encodes space as `+`; only the single blank after `-H Name:` is removed; headers from `-A`, `-e`,
   `-b`, `-u` sit at their command-line position and are dropped when a `-H` names the same header. curl's own default
   `User-Agent` and `Accept` are not modelled.
4. **cmd dialect.** The parser models cmd.exe caret processing followed by the C runtime's argument splitting. The
   exporter doubles only backslash runs before a quote (Chrome doubles every backslash, which curl.exe then receives
   doubled), and Chrome cmd fixtures therefore import with the doubled backslashes curl.exe would actually see.
   Values containing CR cannot pass through cmd.exe; they export through `--variable name=<base64>` with a warning
   (needs curl 8.12+).
5. **Bash `\xHH` and octal escapes** are decoded as UTF-8 when valid, else Latin-1, which recovers Firefox's `\xe9` as
   `é`; real bash would pass the raw byte.
6. **PowerShell** parsing follows PowerShell 7.3+ native argument passing; the exporter warns when 5.1 or 7.2 would
   mangle an argument (embedded `"` or empty values).
7. **Dialect detection** is structural: the line-continuation character decides (`^` cmd, backtick PowerShell, `\`
   bash), then a leading `^"`, then `.exe` with PowerShell-style quoting; quoted content cannot fool it.
8. **Shell syntax that cannot be reproduced** (`$VAR`, `$(...)`, backticks, `%VAR%`, pipes and redirections) is kept
   literally or ends the command, always with a visible warning.
9. **`@file` references** are never read without a chosen base directory (spec 7.2). Files the engine reads at send
   time (`-F @file`, `--data-binary @file`, `--cert`, `--key`) are recorded and checked for existence when a base
   directory is set. Relative paths without a base directory are an error at send time.
10. **Export details**: a `Body.Bytes` body that is valid UTF-8 without NUL exports as text, otherwise as
    `--data-binary @request-body.bin` with a warning; a text body without Content-Type exports `-H 'Content-Type:'`
    so curl does not add its default; disabled headers, pseudo-headers and Content-Length are omitted.

## Settings, state and logging (spec 3.6, 10)

1. One `settings.json` holds settings, recent files, filter presets, column layout, splitter positions and window
   placement. Unknown keys are preserved on save. An unreadable file is kept as `settings.json.bad` and defaults are
   used. Composer history is `composer-history.json`; collections are `collections\*.json`; logs are
   `logs\harlens-yyyyMMdd.log`.
2. The log records file paths, counts and error text. Unhandled exceptions are logged with their stack trace; an
   exception message from the runtime could in principle quote a value, which is accepted to keep crash reports
   useful. Nothing is transmitted.

## Verification status

| Check | Linux container (SDK 10.0.112) | Windows Server 2025, GitHub Actions (SDK 10.0.401) |
| :- | :- | :- |
| `dotnet restore --locked-mode` | Pass | Pass (different SDK patch: the lock files hold) |
| `dotnet build -c Release -warnaserror`, whole solution with WPF | Pass | Pass |
| Core tests (2,269, including every cURL fixture and round trip) | Pass | Pass |
| Net tests (86, loopback only, including HTTP/2, TLS, client certificates) | Pass | Pass |
| Isolation tests: SPEC 3.1 assembly-load test; acceptance test 3 for Core and App | Pass | Pass |
| UI smoke test: real windows, every fixture, every row and body view, filters, themes, tool windows, merge; HarLens.Net still unloaded | Not runnable | Pass |
| 100 MB HAR indexed | 0.5 to 1.2 s | 0.46 s |
| 1.43 GB HAR | 6.7 s, peak working set 269 MB | 5.8 s, peak working set 250 MB |
| 200,000 entries, eight filter expressions, cold first pass | slowest 92 ms | slowest 55 ms |
| Single-file self-contained publish, win-x64 and win-arm64, zipped with LICENSE and notices | Pass, also from `./nuget-offline` only | Pass |

Not yet measured: cold start to interactive window (target 1.5 s) and scrolling 200,000 rows without stutter. Both
need a person at a Windows desktop. An attempt to run the UI under Wine 9 failed inside Wine's DirectWrite font
fallback before the first window rendered; the Windows run above supersedes it.

## Open questions

1. **License**: the placeholder reserves all rights.
2. **HTTP/2 cleartext (h2c)** is not available in SocketsHttpHandler; should the composer offer prior-knowledge h2c
   only, or keep the current fallback to HTTP/1.1?
3. **SAZ** password-protected archives are reported as unsupported. Supporting them needs a zip AES decoder, which
   would be a new dependency.
