#!/usr/bin/env bash
# PreToolUse on mcp__.* :
#   Deny MCP tools that create, upload or download FILES.
#
# Why: every path and phase guard in this project is bound to the built-in
# Edit/Write/NotebookEdit tool names. An MCP server that writes files therefore
# bypasses the protected-path list, the phase gate, the edit log and the
# CalcEngine rebuild latch. Funnelling file mutations through the Write tool is
# what makes those guards meaningful.
#
# Deliberately narrow: only file-shaped operations are denied. Browser tabs,
# form inputs, previews, diagram rendering and search tools are untouched.
#
# jq-free: uses sed/grep for JSON parsing and printf for JSON output.
set -uo pipefail

INPUT=$(cat)
TOOL=$(printf '%s' "$INPUT" | sed -nE 's/.*"tool_name"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/p')
TOOL_LC=$(printf '%s' "$TOOL" | tr '[:upper:]' '[:lower:]')

deny() {
  local reason="$1" esc
  esc=${reason//\\/\\\\}
  esc=${esc//\"/\\\"}
  echo "BLOCKED: $reason" >&2
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"%s"}}\n' "$esc"
  exit 0
}

EXPLICIT_REGEX='(write_file|create_file|new_file|save_file|put_file|delete_file|remove_file|upload_|download_)'
NOUN_REGEX='(file|asset|attachment|document)'
VERB_REGEX='(create|write|upload|download|save|new|delete|remove|overwrite)'

if printf '%s' "$TOOL_LC" | grep -qE "$EXPLICIT_REGEX"; then
  deny "MCP tool '${TOOL}' writes files, which bypasses the protected-path and phase guards bound to Edit/Write/NotebookEdit. Use the Write tool instead."
fi

if printf '%s' "$TOOL_LC" | grep -qE "$NOUN_REGEX" && printf '%s' "$TOOL_LC" | grep -qE "$VERB_REGEX"; then
  deny "MCP tool '${TOOL}' appears to create or modify files, which bypasses the protected-path and phase guards bound to Edit/Write/NotebookEdit. Use the Write tool instead."
fi

exit 0
