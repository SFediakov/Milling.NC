# syntax=docker/dockerfile:1
# Reproducible Linux build. The base image is the only external fetch of the project; packages come
# from third_party/nuget. The digest is the manifest of tag 10.0.201, the SDK version whose bundled
# runtime (10.0.5) matches the vendored runtime packs.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:127d7d4d601ae26b8e04c54efb37e9ce8766931bded0ee59fcd799afd21d6850
WORKDIR /src
COPY . .
RUN bash build.sh
