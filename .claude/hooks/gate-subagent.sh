#!/usr/bin/env bash
# PreToolUse on Agent: refuse, always.
#
# CLAUDE.md, "Subagent policy (hard rule)": "sub-agents are not allowed". Every
# development phase is now the main agent's own work - phases 2 and 3 say to
# research files and the web directly, not to delegate.
#
# There is deliberately NO parameter that can turn this into an allow: not a
# phase, not a model, not a count, not a state file. The previous version keyed
# a model and a per-phase budget off the phase number, which meant the
# prohibition depended on the phase state being correct. It no longer does.
#
# The active-subagent counter is NOT incremented here, because nothing is ever
# spawned through this tool. track-subagent-stop.sh still decrements on
# SubagentStop, and advance.sh / rollback.sh / gate-stop.sh still refuse while
# the counter is above zero. That machinery is kept on purpose: an agent can
# still be started through surfaces this matcher never sees (the Skill tool, or
# an MCP server that spawns a session), and if one of those fires SubagentStop
# the counter must not go negative or strand the phase machine.
#
# jq-free: printf for JSON output. Nothing is parsed, because nothing about the
# payload can change the answer.

set -uo pipefail

# Fail closed on abort. A gate that dies prints nothing, and the runtime reads
# that as "no opinion" and runs the tool.
trap 'rc=$?; if [[ $rc -ne 0 ]]; then printf "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"gate-subagent aborted before reaching a decision (exit $rc). Failing closed.\"}}\n"; fi' EXIT

cat > /dev/null 2>&1 || true

printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Subagents are not allowed. CLAUDE.md states it as a hard rule: every phase, including files pre-research and WEB research, is the main agent'"'"'s own work. There is no phase, model or argument that permits a spawn - do the work directly."}}\n'
echo "BLOCKED: subagent spawn refused - CLAUDE.md forbids sub-agents outright" >&2
exit 0
