#!/usr/bin/env bash
# PostToolUse on Edit|Write|MultiEdit:
#   - Log the edit.
#   - For C source under CalcEngine/native, flag rebuild requirement.
# Phase advancement is handled by .claude/hooks/advance.sh, not by this hook.
#
# jq-free.
set -uo pipefail

STATE_DIR="${CLAUDE_PROJECT_DIR}/.claude/state"
mkdir -p "${STATE_DIR}"
LOG_FILE="${STATE_DIR}/edits.log"

INPUT=$(cat)

FILE_PATH=$(printf '%s' "$INPUT" | sed -nE 's/.*"file_path"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
if [[ -z "$FILE_PATH" ]]; then
  FILE_PATH=$(printf '%s' "$INPUT" | sed -nE 's/.*"path"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
fi
FILE_PATH="${FILE_PATH//\\\\/\\}"

TS=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
echo "${TS} ${FILE_PATH}" >> "${LOG_FILE}"

# CLAUDE.md: changes to CalcEngine/native require CMake rebuild + DLL update.
NORMALIZED="${FILE_PATH//\\//}"
if printf '%s' "$NORMALIZED" | grep -qE 'CalcEngine/native/'; then
  echo "${TS} REBUILD_REQUIRED ${FILE_PATH}" >> "${STATE_DIR}/rebuild_pending.log"
fi
