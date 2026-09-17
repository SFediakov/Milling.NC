#!/usr/bin/env bash
# PostToolUse and PostToolUseFailure on Agent: settle the reservation that
# gate-subagent.sh took for this call (same tool_use_id).
#
#   PostToolUse, status "async_launched"  - the sub-agent runs in the background;
#                                           bind the slot to tool_response.agentId
#                                           so SubagentStop can release it.
#   PostToolUse, any other status          - the tool call returned after the
#                                           sub-agent finished (a foreground run)
#                                           or without one; release the slot now.
#                                           SubagentStop for a foreground run has
#                                           already fired and found no link.
#   PostToolUseFailure                     - the spawn did not happen; release.
#
# Never blocks and never fails the call: it exits 0 on every path and records
# anything unexpected in hook_errors.log, which gate-stop.sh surfaces. If this
# hook does not run at all, the unlinked reservation is reclaimed by
# ledger_reap after LEDGER_RESERVATION_TTL_MIN minutes.
#
# jq-free: sed/grep for JSON parsing.
set -uo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-}"
[[ -z "$PROJECT_DIR" ]] && exit 0
STATE_DIR="${PROJECT_DIR}/.claude/state"
LIB="${PROJECT_DIR}/.claude/hooks/lib/guard-common.sh"
mkdir -p "$STATE_DIR" 2>/dev/null

INPUT=$(cat 2>/dev/null || true)

if ! source "$LIB" 2>/dev/null; then
  printf '%s track-agent-result: guard library missing (%s); a sub-agent slot may now be held until the session restarts\n' \
    "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$LIB" >> "${STATE_DIR}/hook_errors.log"
  exit 0
fi

LEDGER=$(ledger_dir "$STATE_DIR")
EVENT=$(hook_json_string "$INPUT" hook_event_name)
TOOL_USE_ID=$(hook_json_string "$INPUT" tool_use_id)
STATUS=$(hook_json_string "$INPUT" status)
AGENT_ID=$(hook_json_string "$INPUT" agentId)

if [[ -z "$TOOL_USE_ID" ]]; then
  hook_error "$STATE_DIR" "track-agent-result: ${EVENT:-?} payload without tool_use_id; nothing to settle"
  exit 0
fi

if [[ "$EVENT" == "PostToolUse" && "$STATUS" == "async_launched" && -n "$AGENT_ID" ]]; then
  if ledger_link_agent "$LEDGER" "$TOOL_USE_ID" "$AGENT_ID"; then
    subagent_event_log "$STATE_DIR" "link tool_use_id=${TOOL_USE_ID} agent_id=${AGENT_ID} running=$(slot_count "$LEDGER")"
  else
    subagent_event_log "$STATE_DIR" "link-miss tool_use_id=${TOOL_USE_ID} agent_id=${AGENT_ID} (no reservation: spawn not gated or already reaped)"
  fi
  exit 0
fi

ledger_release_reservation "$LEDGER" "$TOOL_USE_ID"
subagent_event_log "$STATE_DIR" "settle ${EVENT:-?} tool_use_id=${TOOL_USE_ID} status=${STATUS:--} agent_id=${AGENT_ID:--} released running=$(slot_count "$LEDGER")"
exit 0
