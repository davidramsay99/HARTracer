# HarLens

A local Windows viewer for HAR (HTTP Archive) files, laid out like Fiddler Classic, with a request composer that
imports, edits, sends and exports cURL commands. Built from [SPEC.md](SPEC.md); deviations and judgment calls are in
[DECISIONS.md](DECISIONS.md).

## Local only

HarLens initiates no network traffic of its own: no telemetry, update check, crash upload, remote fonts or online
help. The Request Composer is the only code that can open a socket, only when you press Send, and only to the host you
typed. **Offline Mode is on at first run**; while it is on, Send is disabled and the network assembly (`HarLens.Net`)
is never loaded. HTML bodies are shown as text; there is no embedded browser. State lives in `%LOCALAPPDATA%\HarLens\`,
or in a `data\` folder beside the exe when a `portable.flag` file sits next to it.

This is enforced, not just intended:

- `HarLens.Core` and `HarLens.App` fail to build if they use `System.Net.Http`, `Sockets`, `WebSockets`, `Dns`,
  `NetworkInformation` and related types (BannedApiAnalyzers, `RS0030` as an error). A test adds such a call and
  checks the build fails.
- A test opens every fixture, filters, searches, inspects and exports, then checks `HarLens.Net.dll` is not in
  `AppDomain.CurrentDomain.GetAssemblies()`.

## What it does

- Opens HAR 1.1 and 1.2 (`.har`, `.json`, `.har.gz`) and Fiddler `.saz` archives, keeping unknown and vendor fields so
  that Save loses nothing. Large files stream into an index; bodies are read on demand. Truncated files load every
  complete entry and report where parsing stopped.
- Session list: sortable, reorderable, hideable columns; custom columns bound to any header or vendor field; status
  coloring; waterfall; page grouping; multi-select summary; comments and color marks saved into the HAR.
- Filter bar with the Chrome DevTools grammar (`status-code:5xx domain:*.contoso.com -mime-type:image/png /regex/`),
  type and status chips, and saved presets.
- Inspectors for request and response: Headers, Query, Cookies, Body (Pretty, Text, Hex, Image, JWT), Raw, Timing,
  plus Initiator, WebSocket frames, Cache, TLS and the raw HAR entry.
- Search across all entries, compare two entries side by side, statistics, CSV export, and a sanitized HAR export
  that previews every redaction before writing.
- Request Composer: cURL import (bash, Windows cmd, PowerShell), export to cURL, Invoke-WebRequest and raw HTTP,
  connect override (`--resolve`, `--connect-to`), client certificates, proxy, redirects recorded hop by hop, exact
  wire bytes, a replay guard for captured credentials, history and collections.

Keyboard: `Ctrl+O` open, `Ctrl+W` close tab, `Ctrl+S` save, `Ctrl+F` find in view, `Ctrl+Shift+F` search all,
`Ctrl+L` filter, `Esc` clear filter, `Ctrl+C` copy URL, `Ctrl+Shift+C` copy as cURL, `Ctrl+R` send to composer,
`Ctrl+Enter` send, `F6` cycle panes, `Ctrl+Plus`/`Ctrl+Minus` font size.

Command line: `HarLens.exe <file.har> [--filter "<expr>"]`.

## Build

Requires the .NET 10 SDK. On Windows:

```powershell
dotnet restore --locked-mode
dotnet build -c Release -warnaserror
dotnet test  -c Release
dotnet publish src/HarLens.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`scripts/package.ps1` does all of that for win-x64 and win-arm64 and writes zips with `HarLens.exe`, `LICENSE` and
`THIRD-PARTY-NOTICES.txt` to `artifacts/`.

Offline builds: run `scripts/vendor-packages.ps1` once on a connected machine to fill `./nuget-offline/`, then
`dotnet restore --locked-mode --configfile NuGet.offline.config` (or `scripts/package.ps1 -Offline`). That restore is
the only point at which a build uses the internet.

Large-file checks (100 MB load time, 200,000-entry filter time, 1 GB working set) generate their fixtures at test
time and run when `HARLENS_LARGE_TESTS=1`:

```powershell
$env:HARLENS_LARGE_TESTS = "1"; dotnet test tests/HarLens.Core.Tests -c Release --filter "FullyQualifiedName~Performance"
```

On Linux and macOS, Core, Net and the isolation tests run normally; the WPF app and its UI smoke test build (with
`EnableWindowsTargeting`) but need Windows to run.

## Layout

```
src/HarLens.Core   HAR model, streaming parser, index, filter, search, sanitizer, cURL, compare, SAZ. No UI, no network.
src/HarLens.Net    Request engine: the only assembly that uses System.Net.*
src/HarLens.App    WPF shell, views, view models
tests/             Core, Net, Isolation (SPEC 3.1), App (Windows UI smoke) tests; fixtures/ (synthetic only)
scripts/           vendor-packages, package, fixture and notices generators
```

## Status

Everything except the user interface has been built and tested; see the verification table in
[DECISIONS.md](DECISIONS.md#verification-status). The WPF app compiles but has not yet been run on Windows; the UI
smoke test and the CI workflow in `.github/workflows/build.yml` exist to do that.
