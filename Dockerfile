# syntax=docker/dockerfile:1
# PLACEHOLDER - implemented by T-010 (docs/DEVELOPMENT_GUIDE.md). Replace this header with the script.
# Purpose: Reproducible Linux build. The base image is the only external fetch of the project.
# Steps:
#   1. FROM mcr.microsoft.com/dotnet/sdk:10.0   (append @sha256:<digest> after the first pull)
#   2. WORKDIR /src
#   3. COPY . .
#   4. RUN bash build.sh
#   5. no apt-get, no curl, no online NuGet source; packages come from third_party/nuget
#   6. result: /src/dist/win-x64 and /src/dist/linux-x64
