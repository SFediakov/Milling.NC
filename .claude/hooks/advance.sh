#!/usr/bin/env bash
# Phase advance for the 8-phase process defined in the root CLAUDE.md.
#
#   bash .claude/hooks/advance.sh              # phases other than 1
#   bash .claude/hooks/advance.sh "trivial"    # phase 1 only
#   bash .claude/hooks/advance.sh "standard"   # phase 1 only
#
# Phase 1 ("Task definition") cannot be left without one of those two strings,
# spelled exactly. CLAUDE.md marks phases 2, 3 and 4 "skip for trivial tasks like
# PR creation"; all three carry the identical condition, so the classification is
# made once, stored in .claude/state/task_class, and it - not the agent - decides
# which phases exist for this task:
#
#   trivial    1 -> 5   (2, 3, 4 skipped)
#   standard   1 -> 2   (nothing skipped)
#
# Refuses at phase 9 (terminal). From there only the user changes the phase, by
# editing .claude/state/current_phase outside Claude Code - the agent's own tools
# are blocked from that file by the guards.
set -uo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
STATE_DIR="${CLAUDE_PROJECT_DIR:-$(dirname "$SCRIPT_DIR")/..}/.claude/state"
if [[ ! -d "$STATE_DIR" ]]; then
  STATE_DIR="${SCRIPT_DIR}/../state"
fi
mkdir -p "$STATE_DIR"

LIB="${SCRIPT_DIR}/lib/guard-common.sh"
if ! source "$LIB" 2>/dev/null; then
  echo "advance: guard library missing or unreadable: $LIB" >&2
  exit 1
fi

CURRENT=$(read_phase "$STATE_DIR")
CLASS=$(read_task_class "$STATE_DIR")
ARG="${*:-}"
# Trim surrounding whitespace only; the interior must match exactly.
ARG="${ARG#"${ARG%%[![:space:]]*}"}"
ARG="${ARG%"${ARG##*[![:space:]]}"}"

# --- subagent-busy gate ------------------------------------------------------
# CLAUDE.md hard rule: the phase cannot change while any subagent is running.
ACTIVE=$(cat "${STATE_DIR}/subagent_count" 2>/dev/null || echo 0)
[[ "$ACTIVE" =~ ^[0-9]+$ ]] || ACTIVE=0
if (( ACTIVE > 0 )); then
  echo "advance: blocked - ${ACTIVE} subagent(s) still active. Wait for SubagentStop before advancing." >&2
  LOCK="${STATE_DIR}/.subagent_count.lock"
  if [[ -e "$LOCK" ]]; then
    OWNER=$(cat "$LOCK" 2>/dev/null)
    if [[ -n "$OWNER" ]] && ! kill -0 "$OWNER" 2>/dev/null; then
      echo "  - Stale lock detected (owner PID ${OWNER} is gone): rm -f .claude/state/.subagent_count.lock" >&2
    else
      echo "  - Counter lock is currently held by PID ${OWNER:-unknown}." >&2
    fi
  fi
  if [[ -s "${STATE_DIR}/hook_errors.log" ]]; then
    echo "  - Recorded hook errors (a failed decrement leaves the counter too high):" >&2
    tail -n 3 "${STATE_DIR}/hook_errors.log" | sed 's/^/      /' >&2
  fi
  echo "  - If the counter is stuck (missed SubagentStop), reset with: echo 0 > .claude/state/subagent_count" >&2
  exit 1
fi

# --- phase model migration ---------------------------------------------------
# A persisted phase number is only meaningful together with the model it was
# written under. An unstamped or stale value is normalised back to the start of
# the process rather than acted on: under the retired 11-phase model, 7 meant
# Execution and here it means Documentation, so guessing would silently grant the
# wrong permissions. One-way normalisation, loud, no translation table.
if ! phase_model_is_current "$STATE_DIR"; then
  RECORDED=$(phase_model_recorded "$STATE_DIR")
  echo "$PHASE_MIN" > "${STATE_DIR}/current_phase"
  rm -f "${STATE_DIR}/task_class" "${STATE_DIR}/spawns_this_phase"
  phase_model_stamp "$STATE_DIR"
  printf '%s migrate phase_model=%s -> %s, phase reset to %s\n' \
    "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "${RECORDED:-none}" "$PHASE_MODEL_VERSION" "$PHASE_MIN" \
    >> "${STATE_DIR}/phase_history.log"
  echo "advance: recorded phase model was '${RECORDED:-none}', this hook set is model ${PHASE_MODEL_VERSION}." >&2
  echo "  The persisted phase number belongs to a retired model and cannot be interpreted." >&2
  echo "  Phase reset to ${PHASE_MIN} ($(phase_name "$PHASE_MIN")) and task class cleared. Start the process from there." >&2
  exit 1
fi

# --- terminal phase ----------------------------------------------------------
if (( CURRENT >= PHASE_DONE )); then
  echo "advance: already at phase ${PHASE_DONE} ($(phase_name "$PHASE_DONE")). Phase changes from here require the user." >&2
  echo "  - The user edits .claude/state/current_phase directly, outside Claude Code." >&2
  echo "  - Claude's own Edit/Write/shell access to that file is blocked on purpose." >&2
  exit 1
fi

# --- phase 1: the task-class gate --------------------------------------------
if [[ "$CURRENT" == "1" ]]; then
  if [[ -z "$ARG" ]]; then
    echo "advance: phase 1 (task definition) requires the task class as the argument." >&2
    echo "  Exactly one of these two strings, verbatim:" >&2
    echo "    bash .claude/hooks/advance.sh \"${CLASS_TRIVIAL}\"    # -> phase 5, skips 2/3/4" >&2
    echo "    bash .claude/hooks/advance.sh \"${CLASS_STANDARD}\"   # -> phase 2, skips nothing" >&2
    echo "  'trivial' is CLAUDE.md's own wording for work like PR creation." >&2
    exit 1
  fi
  if ! class_is_valid "$ARG"; then
    echo "advance: '${ARG}' is not a permitted task class." >&2
    echo "  Permitted, exact and case-sensitive: '${CLASS_TRIVIAL}', '${CLASS_STANDARD}'." >&2
    exit 1
  fi
  CLASS="$ARG"
  printf '%s' "$CLASS" > "${STATE_DIR}/task_class"
elif [[ -n "$ARG" ]]; then
  echo "advance: a task-class argument is only accepted at phase 1. Current phase is ${CURRENT} ($(phase_name "$CURRENT"))." >&2
  exit 1
fi

# --- class must exist from phase 2 onward ------------------------------------
if (( CURRENT > 1 )) && ! class_is_valid "$CLASS"; then
  echo "advance: no valid task class recorded (.claude/state/task_class = '${CLASS}')." >&2
  echo "  The class is set when leaving phase 1. Re-run phase 1 to restore the state machine." >&2
  exit 1
fi

# --- reachability ------------------------------------------------------------
# Catches a hand-edited current_phase that lands in a phase the class skips.
if ! phase_reachable "$CURRENT" "$CLASS"; then
  echo "advance: phase ${CURRENT} ($(phase_name "$CURRENT")) is not reachable for task class '${CLASS}'." >&2
  echo "  That phase is skipped for this class. Fix .claude/state/current_phase, or re-run phase 1." >&2
  exit 1
fi

NEXT=$(phase_next "$CURRENT" "$CLASS") || {
  echo "advance: no transition defined from phase ${CURRENT}." >&2
  exit 1
}

echo "$NEXT" > "${STATE_DIR}/current_phase"
phase_model_stamp "$STATE_DIR"

SKIPPED=""
if (( NEXT > CURRENT + 1 )); then
  for (( p = CURRENT + 1; p < NEXT; p++ )); do
    SKIPPED="${SKIPPED:+${SKIPPED}, }${p} ($(phase_name "$p"))"
  done
fi

printf '%s advance %s -> %s class=%s%s\n' \
  "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$CURRENT" "$NEXT" "${CLASS:-none}" \
  "${SKIPPED:+ skipped=${SKIPPED}}" >> "${STATE_DIR}/phase_history.log"

echo "Phase advanced: ${CURRENT} ($(phase_name "$CURRENT")) -> ${NEXT} ($(phase_name "$NEXT"))"
[[ -n "$CLASS" ]] && echo "Task class: ${CLASS}"
[[ -n "$SKIPPED" ]] && echo "Skipped by class rule: ${SKIPPED}"
exit 0
