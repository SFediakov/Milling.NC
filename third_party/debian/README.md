# Vendored Debian packages

Operating-system libraries the Linux build container needs beyond the .NET SDK base image.
SkiaSharp (Avalonia's renderer) loads `libfontconfig`, which the base image does not carry; without
it the published Linux binary cannot start a window and the rendering tests fail inside Docker.

The closure of `libfontconfig1` (fontconfig configuration, a font family, FreeType and its
dependencies) is downloaded once with `scripts/vendor-debian.sh` from the same pinned base image
the `Dockerfile` uses, and installed there with `dpkg -i` without any network access.

Rules:

- Commit the `.deb` files; `.gitattributes` marks them binary.
- Re-run the script only when the base image digest in `Dockerfile` changes.
- On a Linux desktop the same libraries come from the distribution (`fontconfig`), which every
  X11 desktop already provides.
