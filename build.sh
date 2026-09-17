#!/usr/bin/env bash
# The only build script; identical on Linux and Git Bash for Windows.
#   bash build.sh               restore, build, test, publish win-x64 + linux-x64, assemble dist/
#   bash build.sh --no-publish  restore, build, test
#   bash build.sh --no-test     restore, build, publish
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

CONFIGURATION=Release
SOLUTION=Miller.sln
APP_PROJECT=src/Miller.App
LAUNCHER=launchers/Miller.sh
DIST_DIR=dist
RIDS=(win-x64 linux-x64)

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
  for rid in "${RIDS[@]}"; do
    # dotnet publish overwrites files but never deletes stale ones; each publish starts from an empty folder.
    rm -rf "${DIST_DIR:?}/$rid"
    dotnet publish "$APP_PROJECT" -c "$CONFIGURATION" -r "$rid" --self-contained -p:PublishSingleFile=true -o "$DIST_DIR/$rid"
  done
  cp "$LAUNCHER" "$DIST_DIR/linux-x64/Miller.sh"
  chmod +x "$DIST_DIR/linux-x64/Miller.sh" "$DIST_DIR/linux-x64/Miller"
  echo "build.sh: Windows start file: $DIST_DIR/win-x64/Miller.exe"
  echo "build.sh: Linux start file:   $DIST_DIR/linux-x64/Miller.sh"
fi
