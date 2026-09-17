#!/usr/bin/env bash
# Stop: in task mode, require current_phase >= 9 (the terminal state reached by
# advancing past Reporting) and no pending CalcEngine rebuild before allowing
# the stop. A running subagent never blocks the turn end, by owner decision.
# Respects stop_hook_active to avoid infinite loops.
#
# Two phases may end the turn early, because their CLAUDE.md text explicitly
# involves asking the user something:
#   phase 4  "Planning"  - "Asking questions allowed only in case of missing
#                          blocking information (which prevents in task
#                          execution at all)."
#   phase 6  "Testing"   - "justify test modification reason to user and ask for
#                          test modification approval, only after direct
#                          approval test might be modified."
# Neither is a routine pause: stopping there is for a question the user must
# answer before the work can continue.
#
# Also surfaces .claude/state/hook_errors.log. Hook failures used to be silent,
# and a silent failure blocked later phase changes with no stated cause.
# When the turn is otherwise allowed to end, the errors are reported through
# additionalContext instead of blocking.
#
# Robust to missing jq (Windows Git Bash): uses grep for JSON parsing and
# printf for JSON output.
set -uo pipefail

# Fail closed on abort, in this hook's own JSON shape. A Stop gate that dies
# printed nothing and the turn ended unchecked - the mirror of the PreToolUse
# case, and the same one-line fix.
trap 'rc=$?; if [[ $rc -ne 0 ]]; then printf "{\"decision\":\"block\",\"reason\":\"gate-stop aborted before reaching a decision (exit $rc). Failing closed - the turn cannot end until the guard runs cleanly.\"}\n"; fi' EXIT

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-}"
if [[ -z "$PROJECT_DIR" ]]; then
  printf '{"decision":"block","reason":"CLAUDE_PROJECT_DIR is not set, so the Stop gate cannot read the phase state. Failing closed."}\n'
  exit 0
fi

STATE_DIR="${PROJECT_DIR}/.claude/state"
LIB="${PROJECT_DIR}/.claude/hooks/lib/guard-common.sh"
INPUT=$(cat)

# The hook-edit grant lasts exactly one turn: "until Claude stops thinking".
# It is revoked on every path where the turn is ALLOWED to end, and deliberately
# not on the block path - if the Stop gate refuses and work continues, the agent
# is still thinking and the consent still stands. Written as a plain rm because
# it must also work on the short-circuit below, which returns before the guard
# library is sourced.
revoke_hook_edit_grant() { rm -f "${STATE_DIR}/hook_edit_grant" 2>/dev/null; }

# Loop protection: detect stop_hook_active=true without jq.
if printf '%s' "$INPUT" | grep -q '"stop_hook_active"[[:space:]]*:[[:space:]]*true'; then
  revoke_hook_edit_grant
  exit 0
fi

if ! source "$LIB" 2>/dev/null; then
  printf '{"decision":"block","reason":"Guard library missing or unreadable: %s. Restore .claude/hooks/lib/guard-common.sh before continuing."}\n' "$LIB"
  exit 0
fi

ERRORS=""
if [[ -s "${STATE_DIR}/hook_errors.log" ]]; then
  ERRORS=$(tail -n 5 "${STATE_DIR}/hook_errors.log" | tr '\n' ' ')
fi

# Enforce phase rules only in task mode.
if [[ ! -f "${STATE_DIR}/TASK_MODE" ]]; then
  revoke_hook_edit_grant
  if [[ -n "$ERRORS" ]]; then
    printf '{"hookSpecificOutput":{"hookEventName":"Stop","additionalContext":"Hook errors recorded this session: %s Clear .claude/state/hook_errors.log once handled."}}\n' \
      "$(json_escape "$ERRORS")"
  fi
  exit 0
fi

PHASE=$(read_phase "$STATE_DIR")
MISSING=()

if (( PHASE < PHASE_DONE )) && ! phase_allows_optional_stop "$PHASE"; then
  MISSING+=("current phase is ${PHASE} ($(phase_name "$PHASE")), must reach ${PHASE_DONE}. Only phase 4 (a blocking planning question) and phase 6 (test-modification approval) may end the turn early. Complete each phase's work in chat and run: bash .claude/hooks/advance.sh after each phase - at phase 1 with the exact task class as the argument, at phase 2 once per finished half ('${RESEARCH_HALF_FILES}' | '${RESEARCH_HALF_WEB}').")
fi

# CalcEngine native edits require confirmed rebuild.
if [[ -s "${STATE_DIR}/rebuild_pending.log" ]]; then
  MISSING+=("CalcEngine rebuild not confirmed - clear .claude/state/rebuild_pending.log after rebuilding.")
fi

if [[ ${#MISSING[@]} -gt 0 ]]; then
  REASON="Phase gate (Stop): cannot end turn. ${MISSING[*]}"
  if [[ -n "$ERRORS" ]]; then
    REASON="${REASON} Recorded hook errors: ${ERRORS}"
  fi
  printf '{"decision":"block","reason":"%s"}\n' "$(json_escape "$REASON")"
  exit 0
fi

# The turn is ending for real: the consent expires here.
revoke_hook_edit_grant

if [[ -n "$ERRORS" ]]; then
  printf '{"hookSpecificOutput":{"hookEventName":"Stop","additionalContext":"Hook errors recorded this session: %s Clear .claude/state/hook_errors.log once handled."}}\n' \
    "$(json_escape "$ERRORS")"
fi
exit 0
