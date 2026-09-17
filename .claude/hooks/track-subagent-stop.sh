#!/usr/bin/env bash
# SubagentStop: decrement the active-subagent counter (floor at 0).
# Never blocks - a subagent must always be allowed to finish.
#
# A failed decrement cannot be resolved here, so it is recorded LOUDLY in
# hook_errors.log; advance.sh and gate-stop.sh surface that file. The previous
# version skipped the update silently after a ~19s spin, which produced a
# counter that blocked every later phase change with no explanation.

STATE_DIR="${CLAUDE_PROJECT_DIR}/.claude/state"
LIB="${CLAUDE_PROJECT_DIR}/.claude/hooks/lib/guard-common.sh"
mkdir -p "${STATE_DIR}" 2>/dev/null

# Drain stdin (Claude Code sends hook JSON); we do not need the fields.
cat > /dev/null 2>&1 || true

if ! source "$LIB" 2>/dev/null; then
  printf '%s track-subagent-stop: guard library missing (%s)\n' \
    "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "$LIB" >> "${STATE_DIR}/hook_errors.log"
  exit 0
fi

COUNTER="${STATE_DIR}/subagent_count"
LOCK="${STATE_DIR}/.subagent_count.lock"

if ! lock_acquire "$LOCK"; then
  hook_error "$STATE_DIR" "track-subagent-stop: could not acquire ${LOCK}; subagent_count was NOT decremented and is now too high. Reset with: echo 0 > .claude/state/subagent_count"
  exit 0
fi
trap 'lock_release "$LOCK"' EXIT

current=$(cat "$COUNTER" 2>/dev/null || echo 0)
[[ "$current" =~ ^[0-9]+$ ]] || current=0
if (( current > 0 )); then
  echo $((current - 1)) > "$COUNTER"
else
  echo 0 > "$COUNTER"
fi

exit 0
