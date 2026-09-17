#!/usr/bin/env bash
# SubagentStart: audit only. Records which agent started, with its type, so the
# ledger can be checked against what the runtime actually ran - including
# spawns the spawn gate never sees (Workflow agents report here as their own
# agent_type). It cannot block (the runtime ignores a decision from this event)
# and it takes no slot: admission is gate-subagent.sh's job, and a second
# counter here would double-count every Agent-tool spawn.
#
# jq-free: sed for JSON parsing.
set -uo pipefail

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-}"
[[ -z "$PROJECT_DIR" ]] && exit 0
STATE_DIR="${PROJECT_DIR}/.claude/state"
LIB="${PROJECT_DIR}/.claude/hooks/lib/guard-common.sh"
mkdir -p "$STATE_DIR" 2>/dev/null

INPUT=$(cat 2>/dev/null || true)

source "$LIB" 2>/dev/null || exit 0

LEDGER=$(ledger_dir "$STATE_DIR")
AGENT_ID=$(hook_json_string "$INPUT" agent_id)
AGENT_TYPE=$(hook_json_string "$INPUT" agent_type)
LINKED="no"
[[ -n "$AGENT_ID" && -f "${LEDGER}/agent.${AGENT_ID}" ]] && LINKED="yes"
subagent_event_log "$STATE_DIR" "start agent_id=${AGENT_ID:--} type=${AGENT_TYPE:--} linked=${LINKED} running=$(slot_count "$LEDGER")"
exit 0
