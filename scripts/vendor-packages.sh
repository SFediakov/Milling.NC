#!/usr/bin/env bash
# One-time, online: fills third_party/nuget with every package and runtime pack the solution needs.
# This is the only file in the repository that names an online package source.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ONLINE_SOURCE="https://api.nuget.org/v3/index.json"
PACKAGE_PROPS="$ROOT/Directory.Packages.props"
VENDOR_DIR="$ROOT/out/vendor"
CACHE_DIR="$VENDOR_DIR/cache"
# An empty targeting-pack root makes the SDK download Microsoft.NETCore.App.Ref, the apphost packs
# and the runtime packs as packages instead of resolving them from <dotnet>/packs of this machine.
EMPTY_PACKS_DIR="$VENDOR_DIR/no-packs"
META_PROJECT="$VENDOR_DIR/Vendor.csproj"
TARGET_DIR="$ROOT/third_party/nuget"
RIDS=(win-x64 linux-x64)

rm -rf "$VENDOR_DIR"
mkdir -p "$CACHE_DIR" "$EMPTY_PACKS_DIR" "$TARGET_DIR"

mapfile -t PACKAGES < <(grep -oE '<PackageVersion Include="[^"]+"' "$PACKAGE_PROPS" | sed -E 's/.*Include="([^"]+)"/\1/')
if (( ${#PACKAGES[@]} == 0 )); then
  echo "vendor-packages: no PackageVersion entries found in $PACKAGE_PROPS" >&2
  exit 1
fi

{
  echo '<Project Sdk="Microsoft.NET.Sdk">'
  echo '  <PropertyGroup>'
  echo '    <OutputType>Exe</OutputType>'
  echo '    <SelfContained>true</SelfContained>'
  echo '  </PropertyGroup>'
  echo '  <ItemGroup>'
  for package in "${PACKAGES[@]}"; do
    echo "    <PackageReference Include=\"$package\" />"
  done
  echo '  </ItemGroup>'
  echo '</Project>'
} > "$META_PROJECT"

# Package pruning (.NET 10) reads its data from the real packs folder; keeping it enabled makes the
# vendored graph identical to the graph the solution restore computes.
REAL_PACKS_DIR="$(dotnet msbuild "$META_PROJECT" -getProperty:NetCoreTargetingPackRoot)"

restore() {
  dotnet restore "$META_PROJECT" \
    --source "$ONLINE_SOURCE" \
    --packages "$CACHE_DIR" \
    -p:NetCoreTargetingPackRoot="$EMPTY_PACKS_DIR" \
    -p:PrunePackageTargetingPackRoots="$REAL_PACKS_DIR" \
    "$@"
}

restore
for rid in "${RIDS[@]}"; do
  restore -r "$rid"
done

find "$CACHE_DIR" -type f -name '*.nupkg' -exec cp -f {} "$TARGET_DIR/" \;

RUNTIME_VERSION="$(dotnet msbuild "$META_PROJECT" -getProperty:BundledNETCoreAppPackageVersion)"
REQUIRED_PACKS=("microsoft.netcore.app.ref.${RUNTIME_VERSION}")
for rid in "${RIDS[@]}"; do
  REQUIRED_PACKS+=("microsoft.netcore.app.host.${rid}.${RUNTIME_VERSION}" "microsoft.netcore.app.runtime.${rid}.${RUNTIME_VERSION}")
done
for pack in "${REQUIRED_PACKS[@]}"; do
  if [[ ! -f "$TARGET_DIR/${pack}.nupkg" ]]; then
    echo "vendor-packages: required pack missing after restore: ${pack}.nupkg" >&2
    exit 1
  fi
done

COUNT="$(find "$TARGET_DIR" -maxdepth 1 -type f -name '*.nupkg' | wc -l)"
echo "vendor-packages: $COUNT packages in $TARGET_DIR (runtime $RUNTIME_VERSION)"
echo "vendor-packages: acceptance check: disable the network and run 'dotnet restore Miller.sln'"
