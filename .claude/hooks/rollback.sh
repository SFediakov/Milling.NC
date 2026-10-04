#!/usr/bin/env bash
# Phase rollback: 6 (Testing) -> 1 (Task definition).
#
#   bash .claude/hooks/rollback.sh
#
# CLAUDE.md phase 6: "If any test case failed, add 'separate task' about code fix
# ... into the KPI and return to phase 'task definition'."
#
# This is the ONLY backward transition in the state machine, and it is only legal
# from phase 6. The recorded task class is cleared: phase 1 is the class gate, so
# the class must be re-evaluated for the new task definition rather than
# inherited from the run that just failed.
set -uo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
STATE_DIR="${CLAUDE_PROJECT_DIR:-$(dirname "$SCRIPT_DIR")/..}/.claude/state"
if [[ ! -d "$STATE_DIR" ]]; then
  STATE_DIR="${SCRIPT_DIR}/../state"
fi
mkdir -p "$STATE_DIR"

LIB="${SCRIPT_DIR}/lib/guard-common.sh"
if ! source "$LIB" 2>/dev/null; then
  echo "rollback: guard library missing or unreadable: $LIB" >&2
  exit 1
fi

ROLLBACK_FROM=6
ROLLBACK_TO=1

CURRENT=$(read_phase "$STATE_DIR")

ACTIVE=$(cat "${STATE_DIR}/subagent_count" 2>/dev/null || echo 0)
[[ "$ACTIVE" =~ ^[0-9]+$ ]] || ACTIVE=0
if (( ACTIVE > 0 )); then
  echo "rollback: blocked - ${ACTIVE} subagent(s) still active. The phase cannot change while a subagent runs." >&2
  exit 1
fi

# Same migration rule as advance.sh: a phase number written under a different
# model is not a number this script may act on.
if ! phase_model_is_current "$STATE_DIR"; then
  RECORDED=$(phase_model_recorded "$STATE_DIR")
  echo "$PHASE_MIN" > "${STATE_DIR}/current_phase"
  rm -f "${STATE_DIR}/task_class" "${STATE_DIR}/spawns_this_phase"
  phase_model_stamp "$STATE_DIR"
  printf '%s migrate phase_model=%s -> %s, phase reset to %s\n' \
    "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "${RECORDED:-none}" "$PHASE_MODEL_VERSION" "$PHASE_MIN" \
    >> "${STATE_DIR}/phase_history.log"
  echo "rollback: recorded phase model was '${RECORDED:-none}', this hook set is model ${PHASE_MODEL_VERSION}." >&2
  echo "  Phase reset to ${PHASE_MIN} ($(phase_name "$PHASE_MIN")) and task class cleared." >&2
  exit 1
fi

if [[ "$CURRENT" != "$ROLLBACK_FROM" ]]; then
  echo "rollback: only legal from phase ${ROLLBACK_FROM} ($(phase_name "$ROLLBACK_FROM")). Current phase is ${CURRENT} ($(phase_name "$CURRENT"))." >&2
  echo "  A failing test is the only defined reason to go backwards. Every other phase moves forward with advance.sh." >&2
  exit 1
fi

REASON="${*:-}"

echo "$ROLLBACK_TO" > "${STATE_DIR}/current_phase"
rm -f "${STATE_DIR}/task_class" "${STATE_DIR}/spawns_this_phase"
phase_model_stamp "$STATE_DIR"

printf '%s rollback %s -> %s (task class cleared)%s\n' \
  "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$ROLLBACK_FROM" "$ROLLBACK_TO" \
  "${REASON:+ reason=${REASON}}" >> "${STATE_DIR}/phase_history.log"

echo "Phase rolled back: ${ROLLBACK_FROM} ($(phase_name "$ROLLBACK_FROM")) -> ${ROLLBACK_TO} ($(phase_name "$ROLLBACK_TO"))"
echo "Task class cleared - phase 1 must re-evaluate it for the new task definition."
exit 0
