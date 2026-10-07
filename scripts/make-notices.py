#!/usr/bin/env python3
"""Regenerates THIRD-PARTY-NOTICES.txt from the license files inside the restored NuGet packages.

Run after `dotnet restore` (and a win-x64 publish, which fetches the runtime packs):
    python3 scripts/make-notices.py
Update the version constants when Directory.Packages.props or the SDK changes.
"""
import os

RUNTIME = "10.0.12"
MVVM = "8.4.2"
AVALONEDIT = "6.3.1.120"
HOME = os.environ.get("NUGET_PACKAGES") or os.path.expanduser(os.path.join("~", ".nuget", "packages"))

MIT = open(os.path.join(os.path.dirname(__file__), "mit-license.txt"), encoding="utf-8").read()


def read(path):
    with open(os.path.join(HOME, path), encoding="utf-8-sig") as f:
        return f.read().strip().replace("\r\n", "\n")


def main():
    header = f"""HarLens third-party notices
===========================

HarLens.exe is a self-contained .NET application. It includes the components
listed below. Build-time and test-time packages (Microsoft.CodeAnalysis.BannedApiAnalyzers,
xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk) are not shipped.

  1. .NET runtime {RUNTIME} (Microsoft.NETCore.App)        MIT    https://github.com/dotnet/runtime
  2. Windows Desktop runtime {RUNTIME} (WPF)               MIT    https://github.com/dotnet/wpf
  3. CommunityToolkit.Mvvm {MVVM}                         MIT    https://github.com/CommunityToolkit/dotnet
  4. AvalonEdit {AVALONEDIT}                                MIT    https://github.com/icsharpcode/AvalonEdit

Regenerate this file with scripts/make-notices.py after changing package versions.
"""
    rule = "=" * 60
    parts = [
        header,
        f"\n\n1, 2. .NET runtime and Windows Desktop runtime\n{rule}\n\n" + read(f"microsoft.windowsdesktop.app.runtime.win-x64/{RUNTIME}/LICENSE"),
        "\n\n" + read(f"microsoft.netcore.app.runtime.win-x64/{RUNTIME}/THIRD-PARTY-NOTICES.TXT"),
        f"\n\n3. CommunityToolkit.Mvvm\n{rule}\n\n" + read(f"communitytoolkit.mvvm/{MVVM}/License.md") + "\n\n" + read(f"communitytoolkit.mvvm/{MVVM}/ThirdPartyNotices.txt"),
        f"\n\n4. AvalonEdit\n{rule}\n\nThe MIT License (MIT)\n\nCopyright (c) 2000-2025 AlphaSierraPapa for the SharpDevelop Team\n\n" + MIT,
    ]
    with open("THIRD-PARTY-NOTICES.txt", "w", encoding="utf-8") as f:
        f.write("\n".join(parts))


if __name__ == "__main__":
    main()
