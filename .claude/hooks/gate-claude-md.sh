#!/usr/bin/env bash
# PreToolUse on Edit|Write|NotebookEdit:
#   - The ROOT project CLAUDE.md is denied outright. CLAUDE.md rule: "project
#     Claude.md in root and .claude might be updated only by user". There is no
#     approval prompt for it, because there is no case where Claude edits it.
#     (gate-edit.sh denies the same path; this hook is the second, independent
#     layer so removing one does not open the file.)
#   - CLAUDE.md inside a .claude/ folder still goes to an explicit user prompt.
#     Same ownership rule, but the user retains the button: that file is the
#     process definition and the user may legitimately dictate a change to it
#     mid-session.
#   - Folder-level CLAUDE.md files (Server/, Watchdog/, orchistration/, ...) are
#     untouched here: CLAUDE.md phase 9 requires Claude to maintain them.
#
# Even when defaultMode=bypassPermissions, returning permissionDecision=ask
# requires a user button press.
#
# Pure-bash (no jq dependency) so it works regardless of hook runtime PATH.
set -uo pipefail

INPUT=$(cat)

# Extract "file_path":"..." / "path":"..." / "notebook_path":"..." using sed.
# Captures escaped JSON string body (handles \\ and \" inside the value).
FILE_PATH=$(printf '%s' "$INPUT" | sed -nE 's/.*"file_path"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
if [[ -z "$FILE_PATH" ]]; then
  FILE_PATH=$(printf '%s' "$INPUT" | sed -nE 's/.*"path"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
fi
if [[ -z "$FILE_PATH" ]]; then
  FILE_PATH=$(printf '%s' "$INPUT" | sed -nE 's/.*"notebook_path"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
fi

if [[ -z "$FILE_PATH" ]]; then
  exit 0
fi

# JSON-unescape backslashes: "\\" -> "\", then normalize backslashes to forward
# slashes, then resolve "." and ".." segments. Without the last step
# Server/../CLAUDE.md reached the root document - probing confirmed it. This
# hook stays self-contained (no lib dependency) on purpose, so the resolver is
# duplicated here rather than sourced; tests/hook_tests.sh pins both copies to
# the same behaviour.
UNESCAPED="${FILE_PATH//\\\\/\\}"
NORMALIZED=$(printf '%s' "${UNESCAPED//\\//}" | awk '
{
  lead = ($0 ~ /^\//) ? 1 : 0;
  n = split($0, part, "/");
  top = 0;
  for (i = 1; i <= n; i++) {
    p = part[i];
    if (p == "" || p == ".") continue;
    if (p == ".." && top > 0 && out[top] != "..") { top--; continue }
    out[++top] = p;
  }
  s = "";
  for (i = 1; i <= top; i++) s = s (i > 1 ? "/" : "") out[i];
  if (lead) s = "/" s;
  print s;
}')

# Root CLAUDE.md: absolute form under the project directory, or the bare
# repo-relative form. A path carrying any directory segment in front of the
# filename is a folder-level file and is left alone.
PROJECT_NORM="${CLAUDE_PROJECT_DIR:-}"
PROJECT_NORM="${PROJECT_NORM//\\//}"
PROJECT_NORM="${PROJECT_NORM%/}"
LOWER_NORM=$(printf '%s' "$NORMALIZED" | tr '[:upper:]' '[:lower:]')
LOWER_PROJ=$(printf '%s' "$PROJECT_NORM" | tr '[:upper:]' '[:lower:]')

IS_ROOT=0
[[ -n "$LOWER_PROJ" && "$LOWER_NORM" == "${LOWER_PROJ}/claude.md" ]] && IS_ROOT=1
[[ "$LOWER_NORM" == "claude.md" || "$LOWER_NORM" == "./claude.md" ]] && IS_ROOT=1

if [[ "$IS_ROOT" == "1" ]]; then
  echo "BLOCKED: $NORMALIZED is the root project CLAUDE.md" >&2
  printf '%s\n' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"The root project CLAUDE.md is user-owned and must never be modified by Claude. Lessons learned and rule deviations go in the folder-level CLAUDE.md of the folder that changed (Server/, Watchdog/, orchistration/, ...). If the root file genuinely needs a change, state the proposed wording in chat and let the user apply it."}}'
  exit 0
fi

# Any path containing a `.claude/` segment that ends in CLAUDE.md, at any depth.
if printf '%s' "$NORMALIZED" | grep -qiE '(^|/)\.claude/.*CLAUDE\.md$'; then
  echo "ASK: $NORMALIZED matches .claude/**/CLAUDE.md guard" >&2
  printf '%s\n' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"CLAUDE.md inside a .claude/ folder is user-owned. Explicit user approval (button press) required before modification."}}'
fi

exit 0
