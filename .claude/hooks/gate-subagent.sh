#!/usr/bin/env bash
# PreToolUse on Agent. Three rules, checked in this order:
#
#   1. Model. Root CLAUDE.md: "only opus sub-agents are allowed". A spawn passes
#      when the payload carries exactly one "model" field and its value is
#      SUBAGENT_ALLOWED_MODEL (lib/guard-common.sh). The field MUST be explicit:
#      an omitted model inherits the parent session's model, which this hook
#      cannot see, so it is denied rather than assumed.
#   2. Research block (task mode, phase 2 only). The spawn's description must
#      start with "files:" or "web:" - the half of the block it works for - and
#      each half has a spawn budget for the whole block (RESEARCH_FILES_MAX,
#      RESEARCH_WEB_MAX). No hook field carries the phase half, so the tag is
#      the only way a hook can attribute a spawn to one.
#   3. Concurrency. At most SUBAGENT_MAX_CONCURRENT sub-agents run at the same
#      time, at every phase, task mode or not. The gate takes one of the slot
#      files in the ledger (.claude/state/agents/) with an exclusive create;
#      when none is free the spawn is denied at once - it never waits, because
#      a PreToolUse hook that times out lets the call through.
#
# The slot is taken LAST and released again on any later deny in this script,
# so a refused spawn never holds a slot. The reservation is keyed by the call's
# tool_use_id; track-agent-result.sh (PostToolUse on Agent) binds it to the
# agent id the tool reports, and track-subagent-stop.sh releases it when that
# agent finishes. A payload without a tool_use_id cannot be tracked and is
# denied: the ledger would otherwise hold a slot nobody can release.
#
# Every "model":"..." occurrence is collected, not just one. Picking a single
# occurrence is bypassable from whichever side is not picked: `head -1` used to
# let a decoy "model":"opus" placed ahead of the real "model":"sonnet" through,
# and a greedy last-match would have the mirrored hole. Requiring exactly one
# occurrence, equal to the allowed model, removes the ordering question. An
# escaped decoy inside the prompt text (\"model\":\"sonnet\") does not match the
# unescaped pattern and is correctly ignored.
#
# The value class is "[^"]*" rather than a name charset so that malformed values
# ("opus ", "") are captured and then rejected instead of silently missing the
# pattern. grep returns 1 when nothing matches; this hook runs without `set -e`,
# so the empty assignment falls through to the explicit-model deny below.
#
# Surfaces this hook never sees - the Workflow tool, the Skill tool, MCP
# servers - are not gated here. Their spawns are counted only if the runtime
# reports them (see README, "Sub-agents").
#
# jq-free: uses grep/sed for JSON parsing and printf for JSON output.

set -uo pipefail

# Fail closed on abort. A gate that dies prints nothing, and the runtime reads
# that as "no opinion" and runs the tool.
trap 'rc=$?; if [[ $rc -ne 0 ]]; then printf "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"gate-subagent aborted before reaching a decision (exit $rc). Failing closed.\"}}\n"; fi' EXIT

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-}"
if [[ -z "$PROJECT_DIR" ]]; then
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"CLAUDE_PROJECT_DIR is not set, so the guard cannot locate its rules. Failing closed."}}\n'
  exit 0
fi

STATE_DIR="${PROJECT_DIR}/.claude/state"
LIB="${PROJECT_DIR}/.claude/hooks/lib/guard-common.sh"
if ! source "$LIB" 2>/dev/null; then
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Guard library missing or unreadable: %s"}}\n' "$LIB"
  exit 0
fi

INPUT=$(cat)

# --- 1. model ----------------------------------------------------------------
MODEL_MATCHES=$(printf '%s' "$INPUT" | grep -oE '"model"[[:space:]]*:[[:space:]]*"[^"]*"')

if [[ -z "$MODEL_MATCHES" ]]; then
  deny_pretooluse "Subagent model policy: Agent calls must pass an explicit 'model' field, and the only allowed value is '${SUBAGENT_ALLOWED_MODEL}' (root CLAUDE.md: only opus sub-agents are allowed). An omitted model inherits the session model, which this guard cannot verify."
fi

# Fed by a here-string, not a pipe: a pipe would run the loop in a subshell and
# MODEL_COUNT would be lost. deny_pretooluse exits, so a deny here ends the hook.
MODEL_QUOTE='"'
MODEL_COUNT=0
while IFS= read -r match; do
  [[ -z "$match" ]] && continue
  MODEL_COUNT=$((MODEL_COUNT + 1))
  value=${match#*:}
  value=${value#"${value%%[![:space:]]*}"}
  value=${value#$MODEL_QUOTE}
  value=${value%$MODEL_QUOTE}
  if [[ "$value" != "$SUBAGENT_ALLOWED_MODEL" ]]; then
    deny_pretooluse "Subagent model policy: model '${value:0:40}' is not allowed. The only allowed model is '${SUBAGENT_ALLOWED_MODEL}' (root CLAUDE.md: only opus sub-agents are allowed)."
  fi
done <<< "$MODEL_MATCHES"

if [[ "$MODEL_COUNT" -ne 1 ]]; then
  deny_pretooluse "Subagent model policy: expected exactly one 'model' field, found ${MODEL_COUNT}. A payload carrying more than one is malformed, or is hiding a second model behind an allowed one."
fi

# --- identity of the call ----------------------------------------------------
TOOL_USE_ID=$(hook_json_string "$INPUT" tool_use_id)
if [[ -z "$TOOL_USE_ID" ]] || ! [[ "$TOOL_USE_ID" =~ ^[A-Za-z0-9_.-]+$ ]]; then
  deny_pretooluse "Subagent concurrency policy: the PreToolUse payload carries no usable 'tool_use_id', so this spawn could not be tracked in the ledger and its slot could never be released. Failing closed."
fi

LEDGER=$(ledger_dir "$STATE_DIR")
ledger_reap "$LEDGER"

# --- 2. research block: tag and budget ---------------------------------------
HALF="-"
if [[ -f "${STATE_DIR}/TASK_MODE" ]] && [[ "$(read_phase "$STATE_DIR")" == "$RESEARCH_PHASE" ]]; then
  DESCRIPTION=$(json_unescape "$(hook_json_string "$INPUT" description)")
  case "$DESCRIPTION" in
    "${RESEARCH_HALF_FILES}:"*) HALF="$RESEARCH_HALF_FILES" ;;
    "${RESEARCH_HALF_WEB}:"*)   HALF="$RESEARCH_HALF_WEB" ;;
    *)
      deny_pretooluse "Research block policy: phase ${RESEARCH_PHASE} runs files pre-research and WEB research at the same time, and every sub-agent spawned in it must say which half it works for. Start the Agent 'description' with exactly '${RESEARCH_HALF_FILES}:' or '${RESEARCH_HALF_WEB}:' (budget for this block: ${RESEARCH_FILES_MAX} files agents, ${RESEARCH_WEB_MAX} web agents; used so far: files $(budget_count "$LEDGER" "$RESEARCH_HALF_FILES"), web $(budget_count "$LEDGER" "$RESEARCH_HALF_WEB"))."
      ;;
  esac
fi

# --- 3. concurrency slot -----------------------------------------------------
SLOT=$(slot_acquire "$LEDGER" "$TOOL_USE_ID" "$HALF")
case $? in
  0) ;;
  1) deny_pretooluse "Subagent concurrency policy: ${SUBAGENT_MAX_CONCURRENT} sub-agents are already running (root CLAUDE.md: default max quantity of agents is ${SUBAGENT_MAX_CONCURRENT}). Wait for one to finish before spawning another; do not retry in a loop. Slots are released when a sub-agent stops (SubagentStop) and cleared on a new session." ;;
  *) deny_pretooluse "Subagent concurrency policy: the ledger directory '${LEDGER}' cannot be created, so the limit cannot be enforced. Failing closed." ;;
esac

if [[ "$HALF" != "-" ]]; then
  budget_acquire "$LEDGER" "$HALF" "$TOOL_USE_ID"
  case $? in
    0) ;;
    1)
      ledger_release_reservation "$LEDGER" "$TOOL_USE_ID"
      deny_pretooluse "Research block policy: the '${HALF}' half has used its whole spawn budget for this block ($(research_half_max "$HALF") agents). Continue with the agents already spawned, or mark the half finished: bash .claude/hooks/advance.sh \"${HALF}\"."
      ;;
    *)
      ledger_release_reservation "$LEDGER" "$TOOL_USE_ID"
      deny_pretooluse "Research block policy: the ledger directory '${LEDGER}' cannot be created, so the budget cannot be enforced. Failing closed."
      ;;
  esac
fi

subagent_event_log "$STATE_DIR" "reserve ${SLOT} tool_use_id=${TOOL_USE_ID} half=${HALF} running=$(slot_count "$LEDGER")"
exit 0
