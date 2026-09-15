#!/usr/bin/env bash
# Root start file for Linux and Git Bash on Windows: runs the published binary from dist/ and
# forwards every argument without changing the working directory. Build first: bash build.sh
root="$(dirname "$(readlink -f "$0")")"
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) app="$root/dist/win-x64/Miller.exe" ;;
  *) app="$root/dist/linux-x64/Miller" ;;
esac
if [ ! -x "$app" ]; then
  echo "Miller.sh: $app not found. Run 'bash build.sh' first." >&2
  exit 1
fi
exec "$app" "$@"
