#!/usr/bin/env bash
# UserPromptSubmit: detect implementation intent and inject a phase-aware
# reminder. Soft injection only - enforcement lives in PreToolUse/Stop.
#
# A prompt containing '?' is treated as a question, never as work: that is this
# project's own convention (root CLAUDE.md, "When in request present question
# tag '?' it means question, answer it instead of starting work"). It also
# removes the false positive where asking "why did you fix X?" reset the phase
# to 1 and blocked all edits.
#
# DELIBERATELY SELF-CONTAINED: this hook does NOT source lib/guard-common.sh.
# Every guard that sources it fails closed, which is right for a permission gate
# but wrong for the prompt path - a library that will not parse would then also
# swallow the user's prompt context. The cost is two duplicated constants,
# PHASE_DONE and PHASE_MODEL_VERSION, which tests/hook_tests.sh asserts are equal
# to the library's values so they cannot drift.
#
# jq-free: uses sed for prompt extraction and printf for JSON output.
set -uo pipefail

STATE_DIR="${CLAUDE_PROJECT_DIR}/.claude/state"
INPUT=$(cat)

PHASE_DONE=9
PHASE_MODEL_VERSION=3

# Extract .prompt from JSON input.
PROMPT=$(printf '%s' "$INPUT" | sed -nE 's/.*"prompt"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
PROMPT="${PROMPT//\\\\/\\}"
PROMPT="${PROMPT//\\\"/\"}"

emit() {
  local msg="$1" esc
  esc=${msg//\\/\\\\}
  esc=${esc//\"/\\\"}
  printf '{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"%s"}}\n' "$esc"
  exit 0
}

# --- automation events are not user prompts ----------------------------------
# UserPromptSubmit also fires for harness-generated turns: background-task
# completions, monitor events, CI notifications. They are not requests and must
# never touch the phase state.
#
# Observed live: a "Background command completed" notification arriving at the
# terminal phase carried no '?', so the exit rule below read it as a new task
# and reset the phase to 1 mid-session. The same hole existed before that rule -
# a notification whose text happened to contain "update" or "fix " matched
# TRIGGER_REGEX and reset the phase - it was just rarer.
#
# Matched on structural markers only. Prose that a human might plausibly type
# ("background command failed") is deliberately NOT in this list: treating a
# real request as automation would leave the session ungated at the terminal
# phase, which is the worse failure direction.
AUTOMATION_REGEX='(\[SYSTEM NOTIFICATION - NOT USER INPUT\]|<task-notification>|</task-notification>|<ci-monitor-event>|<system-reminder>|<task-id>)'

# Bounded audit trail. The terminal-phase reset is the one place where a hook
# changes state off the back of untrusted text, so every classification is
# recorded: without this, "the phase moved and nobody knows why" is
# undiagnosable after the fact. Same role session_start_payloads.log plays for
# SessionStart.
log_decision() {
  local kind="$1" phase="$2" excerpt
  excerpt=$(printf '%s' "$PROMPT" | tr '\n\r\t' '   ' | cut -c1-100)
  printf '%s phase=%s decision=%s prompt=%s\n' \
    "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$phase" "$kind" "$excerpt" \
    >> "${STATE_DIR}/prompt_decisions.log" 2>/dev/null
  if [[ -f "${STATE_DIR}/prompt_decisions.log" ]]; then
    tail -n 50 "${STATE_DIR}/prompt_decisions.log" > "${STATE_DIR}/prompt_decisions.log.tmp" 2>/dev/null \
      && mv -f "${STATE_DIR}/prompt_decisions.log.tmp" "${STATE_DIR}/prompt_decisions.log" 2>/dev/null
  fi
}

PHASE_AT_ENTRY=$(cat "${STATE_DIR}/current_phase" 2>/dev/null || echo 1)

if printf '%s' "$PROMPT" | grep -qE "$AUTOMATION_REGEX"; then
  log_decision automation-ignored "$PHASE_AT_ENTRY"
  # A sub-agent stopped with TaskStop fires NO SubagentStop (observed live), so
  # its concurrency slot would be held until the session restarts. The
  # harness's task notification is the one signal that does arrive: its
  # <task-id> is the agent id and its <tool-use-id> the spawning call, which
  # are exactly the two keys the ledger holds. Settling here is idempotent: a
  # slot already released by SubagentStop is simply not found.
  #
  # Run in a subshell with the library sourced there, so that an unparsable
  # library cannot take the prompt path down with it (the reason this hook
  # does not source it at top level).
  if printf '%s' "$PROMPT" | grep -q '<task-notification>'; then
    NOTIFIED_TASK=$(printf '%s' "$PROMPT" | sed -nE 's/.*<task-id>([A-Za-z0-9_.-]+)<\/task-id>.*/\1/p')
    NOTIFIED_CALL=$(printf '%s' "$PROMPT" | sed -nE 's/.*<tool-use-id>([A-Za-z0-9_.-]+)<\/tool-use-id>.*/\1/p')
    NOTIFIED_STATUS=$(printf '%s' "$PROMPT" | sed -nE 's/.*<status>([A-Za-z_]+)<\/status>.*/\1/p')
    (
      source "${CLAUDE_PROJECT_DIR}/.claude/hooks/lib/guard-common.sh" 2>/dev/null || exit 0
      L=$(ledger_dir "$STATE_DIR")
      if [[ -n "$NOTIFIED_TASK" ]] && ledger_release_agent "$L" "$NOTIFIED_TASK"; then
        subagent_event_log "$STATE_DIR" "notification status=${NOTIFIED_STATUS:--} agent_id=${NOTIFIED_TASK} released running=$(slot_count "$L")"
      elif [[ -n "$NOTIFIED_CALL" && -f "$L/res.${NOTIFIED_CALL}" ]]; then
        ledger_release_reservation "$L" "$NOTIFIED_CALL"
        subagent_event_log "$STATE_DIR" "notification status=${NOTIFIED_STATUS:--} tool_use_id=${NOTIFIED_CALL} reservation released running=$(slot_count "$L")"
      fi
    ) 2>/dev/null
  fi
  exit 0
fi

# --- consent to modify the enforcement layer ---------------------------------
# Checked BEFORE the question tag and before the terminal-phase reset, because
# the passphrase carries no '?' and would otherwise be read as a brand new task
# and reset the phase to 1.
#
# The passphrase must appear byte for byte; see the substring note below for
# why it need not be the whole prompt.
#
# This hook is the only writer of hook_edit_grant, and the file is in
# AGENT_LOCKED_STATE_REGEX, so the agent cannot mint the token for itself
# through Edit, Write or the shell at any phase.
HOOK_EDIT_GRANT_PASSPHRASE="yes claude allowed to modify claude settings"
TRIMMED="${PROMPT#"${PROMPT%%[![:space:]]*}"}"
TRIMMED="${TRIMMED%"${TRIMMED##*[![:space:]]}"}"

# Substring match, by explicit user decision: the phrase grants consent wherever
# it appears, so it can be written inline with the request it authorises rather
# than in a message of its own. Still byte-exact and case-sensitive as a phrase.
#
# The trade-off the user accepted: any prompt CONTAINING the phrase grants,
# including a pasted log or a quoted document. Two things reduce the accidental
# path. The guards never print the phrase in a deny reason, so pasting a hook
# error back does not grant. And harness-generated turns are filtered out above,
# before this runs, so a notification carrying the text cannot grant either.
GRANTED=0
if printf '%s' "$PROMPT" | grep -qF "$HOOK_EDIT_GRANT_PASSPHRASE"; then
  : > "${STATE_DIR}/hook_edit_grant"
  GRANTED=1
fi

GRANT_NOTE=""
if (( GRANTED )); then
  GRANT_NOTE=" HOOK EDIT GRANT ACTIVE for this turn only: .claude/settings.json and .claude/hooks/** may be modified until this turn ends. The Stop hook deletes the grant when the turn is allowed to end; after that it must be asked for again."
fi

# The phrase ALONE is consent and nothing else: it must not be read as a new
# task, and must not move the phase. The phrase inside a longer prompt is
# consent PLUS a request, so that case falls through to the normal handling
# below and the grant note is appended to whatever gets emitted.
if (( GRANTED )) && [[ "$TRIMMED" == "$HOOK_EDIT_GRANT_PASSPHRASE" ]]; then
  log_decision hook-edit-granted-only "$PHASE_AT_ENTRY"
  emit "${GRANT_NOTE# } The phase was NOT changed and this prompt is NOT a new task - carry on with what was being discussed."
fi
(( GRANTED )) && log_decision hook-edit-granted-inline "$PHASE_AT_ENTRY"

# Question tag wins over every keyword: answer, do not start work. At the
# terminal phase this is also what KEEPS the session there - see the exit rule
# below, where '?' is the only thing that prevents a reset to phase 1.
if printf '%s' "$PROMPT" | grep -q '?'; then
  log_decision question-no-change "$PHASE_AT_ENTRY"
  (( GRANTED )) && emit "${GRANT_NOTE# }"
  exit 0
fi

# Deliberately broad. A missed match at the terminal phase means a whole task
# runs with no phase enforcement at all, so the cost of a false negative is far
# higher than the cost of a false positive.
TRIGGER_REGEX='(implement|add |fix |build |refactor|create |migrate|change |update |modif|remove |delet|rewrite|optimi[sz]e|improve|repair|correct |adjust|extend|enable |disable |harden|replace |rename|forbid|restrict|make .*(work|faster|better|safer)|clean ?up)'

PHASE=$(cat "${STATE_DIR}/current_phase" 2>/dev/null || echo 1)
[[ "$PHASE" =~ ^[0-9]+$ ]] || PHASE=1
CLASS=$(cat "${STATE_DIR}/task_class" 2>/dev/null || echo "")
TASK_MODE="off"
[[ -f "${STATE_DIR}/TASK_MODE" ]] && TASK_MODE="on"

# --- terminal phase exit rule ------------------------------------------------
# At the terminal phase the question tag is the ONLY discriminator:
#   prompt contains '?'  -> a question. Stay at 9 (handled above, exit 0).
#   prompt has no '?'    -> a new task. Reset to phase 1.
#
# Keyword matching is deliberately NOT consulted here. The terminal phase is
# ungated, so a prompt that starts real work but dodges TRIGGER_REGEX ("one more
# pass on the uploader") used to run the entire task with no phase enforcement
# at all. The '?' convention already separates questions from work everywhere
# else in this project; making it the sole test removes the whole class of
# missed matches.
#
# This is a hook writing the state file, not the agent: current_phase stays
# machine-owned, and the only direction a hook moves it without an explicit
# advance is back to the start of the process.
if [[ "$PHASE" == "$PHASE_DONE" ]]; then
  echo 1 > "${STATE_DIR}/current_phase"
  rm -f "${STATE_DIR}/task_class" "${STATE_DIR}/research_files.done" "${STATE_DIR}/research_web.done"
  rm -f "${STATE_DIR}/agents"/budget.* 2>/dev/null
  printf '%s' "$PHASE_MODEL_VERSION" > "${STATE_DIR}/phase_model"
  log_decision "reset-${PHASE_DONE}-to-1" "$PHASE"
  emit "Phase reset ${PHASE_DONE} -> 1: the previous task was finished and this prompt carries no question tag '?', so it starts a NEW task. Task class cleared. Task mode: ${TASK_MODE}. Work through the phases in order from 1 (Task definition). Advance with: bash .claude/hooks/advance.sh - at phase 1 the exact task class ('trivial' | 'standard') is a required argument and decides whether phases 2, 3 and 4 are skipped. Phase 2 is the research block: files pre-research and WEB research run at the same time and each half is marked finished with advance.sh \"files\" / advance.sh \"web\"; the second mark joins into phase 4. Phases 1-4 and 8 are read only (edits and shell writes outside .claude/ are blocked); 5-7 are free. Sub-agents: only model 'opus' may be spawned (explicit model field required); at most 8 run at the same time; in phase 2 the description must start with 'files:' or 'web:' (budget 5 / 3 per block). The root CLAUDE.md is never editable by Claude.${GRANT_NOTE}"
fi

if ! printf '%s' "$PROMPT" | grep -iqE "$TRIGGER_REGEX"; then
  (( GRANTED )) && emit "${GRANT_NOTE# }"
  exit 0
fi

# The terminal phase is already handled above and never reaches here.
NOTE=""

# Phases 2-8 are mid-task states. An implementation prompt arriving here is
# either a continuation or a brand-new task that would silently inherit the
# permissions of a mid-task phase. The hook cannot tell them apart; the agent
# must ask rather than assume.
if [[ "$PHASE" =~ ^[2-8]$ ]]; then
  NOTE="${NOTE} NOTE: an implementation prompt arrived while the phase is ${PHASE} (mid-task). If it continues the current task, carry on. If it starts a NEW task, ask the user to reset .claude/state/current_phase to 1 - Claude cannot reset it."
fi

emit "Implementation intent detected.${NOTE} Task mode: ${TASK_MODE}. Current phase: ${PHASE}. Task class: ${CLASS:-not yet evaluated}. Follow the phases 1-9 in order. If task mode is off, ask the user to run: touch .claude/state/TASK_MODE. Phase content lives in chat (no marker files). Advance with: bash .claude/hooks/advance.sh - at phase 1 the exact task class ('trivial' | 'standard') is a required argument and decides whether phases 2, 3 and 4 are skipped. Phase 2 is the research block: files pre-research and WEB research run at the same time and each half is marked finished with advance.sh \"files\" / advance.sh \"web\"; the second mark joins into phase 4. Phases 1-4 and 8 are read only (edits and shell writes outside .claude/ are blocked); 5-7 are free. Sub-agents: only model 'opus' may be spawned (explicit model field required); at most 8 run at the same time; in phase 2 the description must start with 'files:' or 'web:' (budget 5 / 3 per block). The root CLAUDE.md is never editable by Claude.${GRANT_NOTE}"
