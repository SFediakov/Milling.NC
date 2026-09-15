#!/usr/bin/env bash
# Starts the published binary next to this script without changing the working directory, so
# relative paths given on the command line keep their meaning.
exec "$(dirname "$(readlink -f "$0")")/Miller" "$@"
