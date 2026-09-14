#!/usr/bin/env bash
# PLACEHOLDER - implemented by T-008 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the script.
# Purpose: The only build script; identical on Linux and Git Bash for Windows.
# Steps:
#   1. set -euo pipefail; cd to the script directory
#   2. parse flags: --no-publish, --no-test
#   3. dotnet restore Miller.sln   (offline: NuGet.config points at third_party/nuget only)
#   4. dotnet build Miller.sln -c Release --no-restore
#   5. dotnet test Miller.sln -c Release --no-build            (skip with --no-test)
#   6. dotnet publish src/Miller.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o dist/win-x64
#   7. dotnet publish src/Miller.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o dist/linux-x64
#   8. cp launchers/Miller.sh dist/linux-x64/Miller.sh; chmod +x dist/linux-x64/Miller.sh dist/linux-x64/Miller
#   9. exit non-zero on any failure; print the dist/ paths at the end
