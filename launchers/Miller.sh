#!/usr/bin/env bash
# Starts the published binary next to this script without changing the working directory, so
# relative paths given on the command line keep their meaning. DRI_PRIME=1 asks a PRIME setup
# (integrated plus discrete GPU) for the discrete one; other setups ignore it.
export DRI_PRIME=1
exec "$(dirname "$(readlink -f "$0")")/Miller" "$@"
