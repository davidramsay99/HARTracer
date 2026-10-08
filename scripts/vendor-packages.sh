#!/usr/bin/env bash
# Same as vendor-packages.ps1, for Linux and macOS build machines.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
feed="${1:-nuget-offline}"
cache="$root/.nuget-vendor-cache"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$root"
dotnet restore Harborer.sln --locked-mode --packages "$cache"
for rid in win-x64 win-arm64; do
  scratch="$(mktemp -d)"
  dotnet publish src/Harborer.App -c Release -r "$rid" --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true --packages "$cache" -o "$scratch"
  rm -rf "$scratch"
done
mkdir -p "$root/$feed"
find "$cache" -name '*.nupkg' -exec cp -f {} "$root/$feed/" \;
echo "Copied $(ls "$root/$feed" | wc -l) packages to $root/$feed. Offline restore: dotnet restore --locked-mode --configfile NuGet.offline.config"
