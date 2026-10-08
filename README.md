# HARborer

A local Windows viewer for HAR (HTTP Archive) files, with a session list beside a request and response inspector,
and a request composer that imports, edits, sends and exports cURL commands.

## Local only

HARborer initiates no network traffic of its own: no telemetry, update check, crash upload, remote fonts or online
help. The Request Composer is the only code that can open a socket, only when you press Send, and only to the host you
typed. **Offline Mode is on at first run**; while it is on, Send is disabled and the network assembly (`Harborer.Net`)
is never loaded. HTML bodies are shown as text; there is no embedded browser. State lives in `%LOCALAPPDATA%\HARborer\`,
or in a `data\` folder beside the exe when a `portable.flag` file sits next to it.

This is enforced, not just intended:

- `Harborer.Core` and `Harborer.App` fail to build if they use `System.Net.Http`, `Sockets`, `WebSockets`, `Dns`,
  `NetworkInformation` and related types (BannedApiAnalyzers, `RS0030` as an error). A test adds such a call and
  checks the build fails.
- A test opens every fixture, filters, searches, inspects and exports, then checks `Harborer.Net.dll` is not in
  `AppDomain.CurrentDomain.GetAssemblies()`.

## What it does

- Opens HAR 1.1 and 1.2 (`.har`, `.json`, `.har.gz`) and `.saz` session archives, keeping unknown and vendor fields so
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

Command line: `HARborer.exe <file.har> [--filter "<expr>"]`.

## Install

1. Go to the [Releases](https://github.com/davidramsay99/HARborer/releases) page.
2. Under the latest release, open **Assets** and download **`HARborer-setup-x64.exe`**. Use `HARborer-setup-arm64.exe`
   only on an ARM-based PC, such as a Snapdragon laptop.
3. Run it. If Windows shows "Windows protected your PC", choose **More info**, then **Run anyway** (the installer is
   not yet code-signed). Choose **Install for me only**; no administrator rights are needed.
4. Start HARborer from the Start menu. To open `.har` files by double-clicking, right-click one, choose
   **Open with > HARborer**, and tick **Always use this app**.

`HARborer-win-x64.zip` is a portable copy: unzip it anywhere and run `HARborer.exe`, nothing is installed. Uninstall
the installed version from Settings > Apps. Code signing: see [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## Build

Requires the .NET 10 SDK. On Windows:

```powershell
dotnet restore --locked-mode
dotnet build -c Release -warnaserror
dotnet test  -c Release
dotnet publish src/Harborer.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`scripts/package.ps1` does all of that for win-x64 and win-arm64 and writes zips with `HARborer.exe`, `LICENSE` and
`THIRD-PARTY-NOTICES.txt` to `artifacts/`.

`installer/Harborer.iss` (Inno Setup 6 or later) turns that output into a per-user installer, no administrator rights
needed: `iscc /DArch=x64 installer\Harborer.iss` writes `artifacts\HARborer-<version>-x64-setup.exe`. It installs to
`%LOCALAPPDATA%\Programs\HARborer`, adds a Start menu entry, optionally adds HARborer to "Open with" for `.har` files,
and registers an uninstaller under Settings > Apps. Settings and history in `%LOCALAPPDATA%\HARborer` are kept on
uninstall. CI builds both installers and checks a silent install and uninstall.

Offline builds: run `scripts/vendor-packages.ps1` once on a connected machine to fill `./nuget-offline/`, then
`dotnet restore --locked-mode --configfile NuGet.offline.config` (or `scripts/package.ps1 -Offline`). That restore is
the only point at which a build uses the internet.

Large-file checks (100 MB load time, 200,000-entry filter time, 1 GB working set) generate their fixtures at test
time and run when `HARBORER_LARGE_TESTS=1`:

```powershell
$env:HARBORER_LARGE_TESTS = "1"; dotnet test tests/Harborer.Core.Tests -c Release --filter "FullyQualifiedName~Performance"
```

On Linux and macOS, Core, Net and the isolation tests run normally; the WPF app and its UI smoke test build (with
`EnableWindowsTargeting`) but need Windows to run.

## Layout

```
src/Harborer.Core   HAR model, streaming parser, index, filter, search, sanitizer, cURL, compare, SAZ. No UI, no network.
src/Harborer.Net    Request engine: the only assembly that uses System.Net.*
src/Harborer.App    WPF shell, views, view models
tests/             Core, Net, Isolation, App (Windows UI smoke) tests; fixtures/ (synthetic only)
scripts/           vendor-packages, package, fixture and notices generators
```

## Dependencies

| Package | Version | Use |
| :- | :- | :- |
| CommunityToolkit.Mvvm | 8.4.2 | View-model source generators |
| AvalonEdit | 6.3.1.120 | Body and raw-message editors |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | 4.14.0 | Build-time network API ban. 5.x needs a newer compiler than SDK 10.0.1xx ships. |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 2.9.3 / 3.1.5 / 18.10.1 | Tests |

Python 3 is needed only to regenerate committed files (`scripts/fixtures/make_fixtures.py`, `scripts/make-notices.py`).

## Known limitations

- A custom Host header in the composer also sets the TLS server name and certificate check, unlike curl, which uses
  the URL host. Connect overrides (`--resolve`, `--connect-to`) keep the URL host in both.
- HTTP/2 over plain http (h2c) falls back to HTTP/1.1 with a notice. HTTP/3 is never attempted.
- Password-protected `.saz` archives are reported as unsupported.
- The exe is unsigned. Third-party antivirus can delay start and file opening until HARborer is excluded.
- On Windows 10 the Fluent theme may render differently from Windows 11.

## Verification

| Check | Linux (SDK 10.0.112) | Windows Server 2025, GitHub Actions (SDK 10.0.401) |
| :- | :- | :- |
| `dotnet restore --locked-mode`; `dotnet build -c Release -warnaserror` | Pass | Pass |
| Core, Net and Isolation tests | Pass | Pass |
| UI smoke test (real windows, every fixture, filters, themes, tool windows, merge) | Not runnable | Pass |
| 100 MB HAR indexed | 0.5 to 1.2 s | 0.46 s |
| 1.43 GB HAR | 6.7 s, peak working set 269 MB | 5.8 s, peak working set 250 MB |
| 200,000 entries, eight filter expressions, cold first pass | slowest 92 ms | slowest 55 ms |
| Single-file publish, win-x64 and win-arm64 | Pass | Pass |

Not yet measured: cold start to an interactive window, and scrolling 200,000 rows.

## Open questions

1. **h2c**: offer prior-knowledge HTTP/2 over plain http, or keep the HTTP/1.1 fallback?
2. **Password-protected SAZ**: support needs a zip AES decoder, a new dependency.

## License

Copyright (C) 2026 davidramsay99

HARborer is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License
as published by the Free Software Foundation, version 3 or (at your option) any later version. It is distributed in
the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
FITNESS FOR A PARTICULAR PURPOSE. See [LICENSE](LICENSE) for the full text.

Bundled third-party components keep their own permissive licenses (MIT, Apache-2.0, BSD, zlib, Unicode), listed in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt); all are compatible with GPL-3.0.

