<#
.SYNOPSIS
  Builds, tests and publishes HARborer, then writes one zip per architecture containing
  HARborer.exe, LICENSE and THIRD-PARTY-NOTICES.txt. No installer.

.PARAMETER Offline
  Restore from ./nuget-offline (filled by vendor-packages.ps1) instead of nuget.org.
#>
param([switch]$Offline, [switch]$SkipTests)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
Push-Location $root
try {
    $restoreArgs = @("--locked-mode")
    if ($Offline) { $restoreArgs += @("--configfile", "NuGet.offline.config") }
    dotnet restore @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "restore failed" }
    dotnet build -c Release -warnaserror --no-restore
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
    if (-not $SkipTests) {
        dotnet test -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "tests failed" }
    }

    $version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $out = Join-Path $root "artifacts"
    New-Item -ItemType Directory -Force $out | Out-Null
    foreach ($rid in "win-x64", "win-arm64") {
        $publish = Join-Path $out "publish-$rid"
        dotnet publish src/Harborer.App -c Release -r $rid --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $publish
        if ($LASTEXITCODE -ne 0) { throw "publish for $rid failed" }
        $stage = Join-Path $out "HARborer-$version-$rid"
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $stage | Out-Null
        Copy-Item (Join-Path $publish "HARborer.exe") $stage
        Copy-Item LICENSE, THIRD-PARTY-NOTICES.txt $stage
        $zip = "$stage.zip"
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
        Write-Host "Wrote $zip"
    }
}
finally {
    Pop-Location
}
