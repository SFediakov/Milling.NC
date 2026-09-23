#!/usr/bin/env bash
# The only build script; identical on Linux and Git Bash for Windows.
#   bash build.sh               restore, build, test, publish the host runtime, assemble dist/
#   bash build.sh --no-publish  restore, build, test
#   bash build.sh --no-test     restore, build, publish
# The toolpath generation is the native C library src/Miller.Native, built by CMake during the build
# with the host's compiler (MSVC on Windows, gcc on Linux). Without a cross compiler each system
# publishes its own runtime: win-x64 on Windows, linux-x64 on Linux.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

CONFIGURATION=Release
SOLUTION=Miller.sln
APP_PROJECT=src/Miller.App
LAUNCHER=launchers/Miller.sh
DIST_DIR=dist
case "$(uname -s)" in
  Linux*) RIDS=(linux-x64) ;;
  *) RIDS=(win-x64) ;;
esac

RUN_TEST=true
RUN_PUBLISH=true
for arg in "$@"; do
  case "$arg" in
    --no-test) RUN_TEST=false ;;
    --no-publish) RUN_PUBLISH=false ;;
    *)
      echo "build.sh: unknown argument '$arg' (allowed: --no-test, --no-publish)" >&2
      exit 2
      ;;
  esac
done

dotnet restore "$SOLUTION"
dotnet build "$SOLUTION" -c "$CONFIGURATION" --no-restore

if [[ "$RUN_TEST" == true ]]; then
  dotnet test "$SOLUTION" -c "$CONFIGURATION" --no-build
fi

if [[ "$RUN_PUBLISH" == true ]]; then
  # dotnet publish overwrites files but never deletes stale ones; dist/ starts empty, so it never
  # keeps an older package of the runtime this system does not build.
  rm -rf "${DIST_DIR:?}"
  for rid in "${RIDS[@]}"; do
    dotnet publish "$APP_PROJECT" -c "$CONFIGURATION" -r "$rid" --self-contained -p:PublishSingleFile=true -o "$DIST_DIR/$rid"
  done
  if [[ "${RIDS[0]}" == linux-x64 ]]; then
    cp "$LAUNCHER" "$DIST_DIR/linux-x64/Miller.sh"
    chmod +x "$DIST_DIR/linux-x64/Miller.sh" "$DIST_DIR/linux-x64/Miller"
    echo "build.sh: Linux start file:   $DIST_DIR/linux-x64/Miller.sh"
  else
    echo "build.sh: Windows start file: $DIST_DIR/win-x64/Miller.exe"
  fi
fi
