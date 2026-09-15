#!/usr/bin/env bash
# One-time, online: downloads the Debian packages the Linux build container needs beyond the base
# image (fontconfig for SkiaSharp text rendering, used by the app and by the rendering tests)
# into third_party/debian. The Dockerfile installs them with dpkg and never touches the network.
# The download runs inside the same pinned base image as the Dockerfile, so the packages match it.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET_DIR="$ROOT/third_party/debian"
STAGE_DIR="$ROOT/out/debian"
PACKAGES=(libfontconfig1)

IMAGE="$(grep -oE '^FROM [^ ]+' "$ROOT/Dockerfile" | head -1 | cut -d' ' -f2)"
if [[ -z "$IMAGE" ]]; then
  echo "vendor-debian: no FROM line found in Dockerfile" >&2
  exit 1
fi

mkdir -p "$STAGE_DIR" "$TARGET_DIR"
rm -f "$STAGE_DIR"/*.deb
MSYS_NO_PATHCONV=1 docker run --rm -v "$STAGE_DIR:/out" "$IMAGE" sh -c \
  "apt-get update -qq >/dev/null && apt-get install -y -qq --download-only --no-install-recommends ${PACKAGES[*]} >/dev/null && cp /var/cache/apt/archives/*.deb /out/"
rm -f "$TARGET_DIR"/*.deb
cp "$STAGE_DIR"/*.deb "$TARGET_DIR"/
echo "vendor-debian: $(ls "$TARGET_DIR"/*.deb | wc -l) packages in $TARGET_DIR"
