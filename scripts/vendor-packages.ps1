<#
.SYNOPSIS
  Fills ./nuget-offline/ with every package the build, the tests and both publishes need,
  so that later builds restore with no internet (SPEC 11).

.DESCRIPTION
  This is the single point at which internet is used, and only on the build machine.
  Afterwards:
    dotnet restore --locked-mode --configfile NuGet.offline.config
#>
param([string]$Feed = "nuget-offline")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $root ".nuget-vendor-cache"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

Push-Location $root
try {
    # Restore into a private packages folder so that exactly the needed packages are collected.
    dotnet restore HarLens.sln --locked-mode --packages $cache
    if ($LASTEXITCODE -ne 0) { throw "restore failed" }
    foreach ($rid in "win-x64", "win-arm64") {
        # Self-contained publish also needs the runtime and apphost packs for each RID; a throwaway
        # publish is the reliable way to fetch exactly those.
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ("harlens-vendor-" + [Guid]::NewGuid().ToString("N"))
        dotnet publish src/HarLens.App -c Release -r $rid --self-contained true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true --packages $cache -o $scratch
        if ($LASTEXITCODE -ne 0) { throw "publish for $rid failed" }
        Remove-Item $scratch -Recurse -Force
    }

    $dest = Join-Path $root $Feed
    New-Item -ItemType Directory -Force $dest | Out-Null
    $count = 0
    Get-ChildItem $cache -Recurse -Filter *.nupkg | ForEach-Object {
        Copy-Item $_.FullName $dest -Force
        $count++
    }
    Write-Host "Copied $count packages to $dest. Offline restore: dotnet restore --locked-mode --configfile NuGet.offline.config"
}
finally {
    Pop-Location
}
