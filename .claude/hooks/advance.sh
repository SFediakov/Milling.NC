#!/usr/bin/env bash
# Phase advance for the process defined in the root CLAUDE.md.
#
#   bash .claude/hooks/advance.sh              # phases 4-8
#   bash .claude/hooks/advance.sh "trivial"    # phase 1 only
#   bash .claude/hooks/advance.sh "standard"   # phase 1 only
#   bash .claude/hooks/advance.sh "files"      # phase 2 only: files pre-research finished
#   bash .claude/hooks/advance.sh "web"        # phase 2 only: WEB research finished
#
# Phase 1 ("Task definition") cannot be left without one of the two class
# strings, spelled exactly. CLAUDE.md marks phases 2, 3 and 4 "skip for trivial
# tasks like PR creation"; all three carry the identical condition, so the
# classification is made once, stored in .claude/state/task_class, and it - not
# the agent - decides which phases exist for this task:
#
#   trivial    1 -> 5   (2, 3, 4 skipped)
#   standard   1 -> 2   (nothing skipped)
#
# Phase 2 is the research block: files pre-research and WEB research run at the
# same time. It cannot be left with a bare advance. Each half is marked finished
# with its own call, in either order, and the second mark joins the block into
# phase 4. A half already marked is refused, so a mark can never count twice.
# The marks live in .claude/state/research_<half>.done, which the agent cannot
# write. The number 3 is retired: it is never produced here.
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

LEDGER=$(ledger_dir "$STATE_DIR")
CURRENT=$(read_phase "$STATE_DIR")
CLASS=$(read_task_class "$STATE_DIR")
ARG="${*:-}"
# Trim surrounding whitespace only; the interior must match exactly.
ARG="${ARG#"${ARG%%[![:space:]]*}"}"
ARG="${ARG%"${ARG##*[![:space:]]}"}"

log_history() {
  printf '%s %s\n' "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$1" >> "${STATE_DIR}/phase_history.log"
}

# --- phase model migration ---------------------------------------------------
# A persisted phase number is only meaningful together with the model it was
# written under. An unstamped or stale value is normalised back to the start of
# the process rather than acted on: under the retired 11-phase model, 7 meant
# Execution and here it means Documentation; under model 2, 3 meant WEB research
# and here it is not a phase. Guessing would silently grant the wrong
# permissions. One-way normalisation, loud, no translation table.
if ! phase_model_is_current "$STATE_DIR"; then
  RECORDED=$(phase_model_recorded "$STATE_DIR")
  echo "$PHASE_MIN" > "${STATE_DIR}/current_phase"
  rm -f "${STATE_DIR}/task_class"
  research_flags_clear "$STATE_DIR"
  budget_clear "$LEDGER"
  phase_model_stamp "$STATE_DIR"
  log_history "migrate phase_model=${RECORDED:-none} -> ${PHASE_MODEL_VERSION}, phase reset to ${PHASE_MIN}"
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
    echo "    bash .claude/hooks/advance.sh \"${CLASS_STANDARD}\"   # -> phase 2 (research block), skips nothing" >&2
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
fi

# --- class must exist from phase 2 onward ------------------------------------
if (( CURRENT > 1 )) && ! class_is_valid "$CLASS"; then
  echo "advance: no valid task class recorded (.claude/state/task_class = '${CLASS}')." >&2
  echo "  The class is set when leaving phase 1. Re-run phase 1 to restore the state machine." >&2
  exit 1
fi

# --- reachability ------------------------------------------------------------
# Catches a hand-edited current_phase that lands in a phase the class skips, or
# on the retired number 3.
if ! phase_reachable "$CURRENT" "$CLASS"; then
  echo "advance: phase ${CURRENT} ($(phase_name "$CURRENT")) is not reachable for task class '${CLASS}'." >&2
  echo "  That phase is skipped for this class or is not a phase of model ${PHASE_MODEL_VERSION}. Fix .claude/state/current_phase, or re-run phase 1." >&2
  exit 1
fi

# --- phase 2: the research block join ----------------------------------------
if [[ "$CURRENT" == "$RESEARCH_PHASE" ]]; then
  if [[ -z "$ARG" ]]; then
    echo "advance: phase 2 is the research block; a bare advance does not leave it." >&2
    echo "  Mark each half finished with its own call, in either order:" >&2
    echo "    bash .claude/hooks/advance.sh \"${RESEARCH_HALF_FILES}\"   # files pre-research finished" >&2
    echo "    bash .claude/hooks/advance.sh \"${RESEARCH_HALF_WEB}\"     # WEB research finished" >&2
    echo "  The second mark joins the block into phase 4 ($(phase_name 4))." >&2
    echo "  Marked so far: files=$(research_half_done "$STATE_DIR" "$RESEARCH_HALF_FILES" && printf yes || printf no) web=$(research_half_done "$STATE_DIR" "$RESEARCH_HALF_WEB" && printf yes || printf no)" >&2
    exit 1
  fi
  if ! research_half_is_valid "$ARG"; then
    echo "advance: '${ARG}' is not a half of the research block." >&2
    echo "  Permitted, exact and case-sensitive: '${RESEARCH_HALF_FILES}', '${RESEARCH_HALF_WEB}'." >&2
    exit 1
  fi
  if research_half_done "$STATE_DIR" "$ARG"; then
    echo "advance: '${ARG}' is already marked finished; a half cannot be marked twice." >&2
    echo "  Marked so far: files=$(research_half_done "$STATE_DIR" "$RESEARCH_HALF_FILES" && printf yes || printf no) web=$(research_half_done "$STATE_DIR" "$RESEARCH_HALF_WEB" && printf yes || printf no)" >&2
    exit 1
  fi
  printf '%s finished %s\n' "$ARG" "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" > "$(research_flag_path "$STATE_DIR" "$ARG")"
  log_history "mark ${ARG} finished at phase ${RESEARCH_PHASE} class=${CLASS}"
  if ! research_both_done "$STATE_DIR"; then
    echo "Research half marked finished: ${ARG}. Phase stays ${RESEARCH_PHASE} ($(phase_name "$RESEARCH_PHASE"))."
    echo "Marked so far: files=$(research_half_done "$STATE_DIR" "$RESEARCH_HALF_FILES" && printf yes || printf no) web=$(research_half_done "$STATE_DIR" "$RESEARCH_HALF_WEB" && printf yes || printf no). Mark the other half to join into phase 4."
    exit 0
  fi
  echo "Research half marked finished: ${ARG}. Both halves are finished - joining."
elif [[ "$CURRENT" != "1" && -n "$ARG" ]]; then
  echo "advance: an argument is only accepted at phase 1 (task class) and phase 2 (research half). Current phase is ${CURRENT} ($(phase_name "$CURRENT"))." >&2
  exit 1
fi

NEXT=$(phase_next "$CURRENT" "$CLASS") || {
  echo "advance: no transition defined from phase ${CURRENT}." >&2
  exit 1
}

echo "$NEXT" > "${STATE_DIR}/current_phase"
phase_model_stamp "$STATE_DIR"

# The block's own state does not outlive it: the join flags and the per-half
# spawn budgets are cleared on the way in (a fresh block) and on the way out.
if [[ "$NEXT" == "$RESEARCH_PHASE" || "$CURRENT" == "$RESEARCH_PHASE" ]]; then
  research_flags_clear "$STATE_DIR"
  budget_clear "$LEDGER"
fi

SKIPPED=""
if [[ "$CURRENT" == "1" && "$NEXT" == "5" ]]; then
  SKIPPED="2 (research block: files pre-research + WEB research), 4 ($(phase_name 4))"
fi

log_history "advance ${CURRENT} -> ${NEXT} class=${CLASS:-none}${SKIPPED:+ skipped=${SKIPPED}}"

echo "Phase advanced: ${CURRENT} ($(phase_name "$CURRENT")) -> ${NEXT} ($(phase_name "$NEXT"))"
[[ -n "$CLASS" ]] && echo "Task class: ${CLASS}"
[[ -n "$SKIPPED" ]] && echo "Skipped by class rule: ${SKIPPED}"
if [[ "$NEXT" == "$RESEARCH_PHASE" ]]; then
  echo "Research block: run files pre-research and WEB research at the same time. Sub-agents spawned now must carry 'files:' or 'web:' at the start of their description (budget ${RESEARCH_FILES_MAX} files / ${RESEARCH_WEB_MAX} web for the block). Mark each half with: bash .claude/hooks/advance.sh \"${RESEARCH_HALF_FILES}\" / \"${RESEARCH_HALF_WEB}\"."
fi
exit 0
