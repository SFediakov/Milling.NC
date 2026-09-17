#!/usr/bin/env bash
# SubagentStop: release the concurrency slot of the agent that just finished.
# Never blocks - a sub-agent must always be allowed to finish (a "block" here
# would keep it running, not cancel it), so this hook exits 0 on every path.
#
# The slot is found through the link track-agent-result.sh wrote from the Agent
# tool's response (agent.<agent_id>). An agent with no link was not admitted by
# gate-subagent.sh - a Workflow or Skill spawn, a resumed run, or a foreground
# run whose PostToolUse releases the slot itself - and is only logged.
#
# A failed release cannot be repaired here, so it is recorded LOUDLY in
# hook_errors.log, which gate-stop.sh surfaces; the slot is then held until the
# next session start clears the ledger.
#
# jq-free: sed for JSON parsing.
set -uo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-}"
[[ -z "$PROJECT_DIR" ]] && exit 0
STATE_DIR="${PROJECT_DIR}/.claude/state"
LIB="${PROJECT_DIR}/.claude/hooks/lib/guard-common.sh"
mkdir -p "$STATE_DIR" 2>/dev/null

INPUT=$(cat 2>/dev/null || true)

if ! source "$LIB" 2>/dev/null; then
  printf '%s track-subagent-stop: guard library missing (%s); a sub-agent slot was NOT released\n' \
    "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$LIB" >> "${STATE_DIR}/hook_errors.log"
  exit 0
fi

LEDGER=$(ledger_dir "$STATE_DIR")
AGENT_ID=$(hook_json_string "$INPUT" agent_id)
AGENT_TYPE=$(hook_json_string "$INPUT" agent_type)

if [[ -z "$AGENT_ID" ]]; then
  hook_error "$STATE_DIR" "track-subagent-stop: payload without agent_id; no slot released"
  exit 0
fi

if ledger_release_agent "$LEDGER" "$AGENT_ID"; then
  subagent_event_log "$STATE_DIR" "stop agent_id=${AGENT_ID} type=${AGENT_TYPE:--} released running=$(slot_count "$LEDGER")"
else
  subagent_event_log "$STATE_DIR" "stop agent_id=${AGENT_ID} type=${AGENT_TYPE:--} untracked (not admitted by the gate) running=$(slot_count "$LEDGER")"
fi
exit 0
