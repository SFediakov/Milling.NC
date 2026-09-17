#!/usr/bin/env bash
# PreToolUse on Edit|Write|NotebookEdit:
#   - Always block writes to protected paths.
#   - Always block writes to the root CLAUDE.md (user-owned).
#   - Always block writes to the phase-state files (current_phase, task_class,
#     TASK_MODE) - the state machine is only real if the agent cannot move itself.
#   - Always ASK before writing the enforcement layer itself (settings.json and
#     anything under .claude/hooks/). Those files stay maintainable, but not
#     silently: switching the guards off now costs a user button press.
#   - Otherwise allow .claude/ writes (state, scratch).
#   - In task mode, block edits outside .claude/ in every read-only phase:
#     1-4 (task definition, files pre-research, WEB research, planning) and
#     8 (reporting).
#   - Phases 5-7 (execution, testing, documentation) and 9 are free.
#
# Every path is normalized (separators collapsed, "." and ".." resolved) and
# matched case-insensitively before any decision. Probing found both gaps:
# Server/../CLAUDE.md reached the root document, and .claude/state/task_mode
# reached the master switch on a case-insensitive filesystem.
#
# The EXIT trap is the other half of failing closed. An abort - unbound
# variable, missing library, killed process - used to exit non-zero having
# printed nothing, which the runtime reads as "no opinion" and the write
# proceeded. Now any non-zero exit emits a deny.
#
# jq-free: uses sed/grep for JSON parsing and printf for JSON output.
set -uo pipefail

trap 'rc=$?; if [[ $rc -ne 0 ]]; then printf "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"gate-edit aborted before reaching a decision (exit $rc). Failing closed.\"}}\n"; fi' EXIT

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-}"
if [[ -z "$PROJECT_DIR" ]]; then
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"CLAUDE_PROJECT_DIR is not set, so the guard cannot locate its rules or the phase state. Failing closed."}}\n'
  exit 0
fi

STATE_DIR="${PROJECT_DIR}/.claude/state"
LIB="${PROJECT_DIR}/.claude/hooks/lib/guard-common.sh"

# Fail closed: a guard that cannot load its rules must not allow the call.
if ! source "$LIB" 2>/dev/null; then
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Guard library missing or unreadable: %s"}}\n' "$LIB"
  exit 0
fi

INPUT=$(cat)

FILE_PATH=$(hook_json_string "$INPUT" file_path)
if [[ -z "$FILE_PATH" ]]; then
  FILE_PATH=$(hook_json_string "$INPUT" path)
fi
if [[ -z "$FILE_PATH" ]]; then
  FILE_PATH=$(hook_json_string "$INPUT" notebook_path)
fi
FILE_PATH="${FILE_PATH//\\\\/\\}"
NORMALIZED=$(clean_path "$FILE_PATH")

if printf '%s' "$NORMALIZED" | grep -qiE "$PROTECTED_WRITE_REGEX"; then
  deny_pretooluse "Path is in the protected list (.env, deployment.json, cloudflared credentials, .git, *.db, payment_notes_archive)"
fi

if is_root_claude_md "$NORMALIZED"; then
  deny_pretooluse "The root CLAUDE.md is user-owned and cannot be modified by Claude under any phase. Folder-level CLAUDE.md files (Server/, Watchdog/, orchistration/, ...) remain editable and are where lessons learned belong."
fi

if printf '%s' "$NORMALIZED" | grep -qiE "$AGENT_LOCKED_STATE_REGEX"; then
  deny_pretooluse "Phase state is machine-owned: current_phase, task_class, TASK_MODE, the research-block marks (research_files.done, research_web.done) and the sub-agent ledger (agents/) cannot be written by Claude. Move forward with 'bash .claude/hooks/advance.sh' (phase 1 requires the exact task class, phase 2 the finished half) or back with 'bash .claude/hooks/rollback.sh' from phase 6. Only the user edits these files directly, outside Claude Code."
fi

# The enforcement layer itself. Maintainable, but only with per-turn consent.
#
# This was an `ask` first. Probing showed permissionDecision:"ask" did NOT stop
# the write in this client, despite gate-claude-md.sh asserting that it would -
# so the protection was theatre. A deny keyed on a token the agent cannot mint
# is the version that actually holds, and it reuses the mechanism already proven
# by TASK_MODE: a file in AGENT_LOCKED_STATE_REGEX.
if printf '%s' "$NORMALIZED" | grep -qiE "$HOOK_INFRA_REGEX" && ! hook_edit_grant_active "$STATE_DIR"; then
  # The passphrase is deliberately NOT quoted in this message. It grants on a
  # substring match, so printing it here would mean that pasting this very error
  # back into chat silently creates the grant.
  deny_pretooluse "'${NORMALIZED##*/}' is part of the enforcement layer (settings.json or .claude/hooks/**) and there is no active consent for this turn. Ask the user, in chat, exactly: \"${HOOK_EDIT_GRANT_QUESTION}\" and then wait for them to reply with the consent passphrase (it is recorded in .claude/hooks/README.md; do not paste it from this message). Consent lasts until this turn ends and must then be asked for again. Claude cannot create it - .claude/state/hook_edit_grant is machine-owned."
fi

# .claude/ writes otherwise permitted: state, scratch.
PROJECT_NORM=$(normalize_path "$PROJECT_DIR")
case "$NORMALIZED" in
  "${PROJECT_NORM}/.claude/"*) exit 0 ;;
  ".claude/"*) exit 0 ;;
esac

# The session scratchpad is not project work and is writable at every phase.
# gate-bash.sh exempts it too; the two surfaces must agree on what is writable,
# or a read-only phase blocks a scratch file from Write while allowing the
# identical `>` from the shell.
if is_agent_writable_target "$NORMALIZED"; then
  exit 0
fi

# Outside task mode: no phase gating.
if [[ ! -f "${STATE_DIR}/TASK_MODE" ]]; then
  exit 0
fi

PHASE=$(read_phase "$STATE_DIR")

if phase_is_readonly "$PHASE"; then
  if [[ "$PHASE" == "8" ]]; then
    deny_pretooluse "Phase gate: current phase is 8 ($(phase_name 8)) - read only. Produce the report in chat, then run: bash .claude/hooks/advance.sh"
  fi
  deny_pretooluse "Phase gate: current phase is ${PHASE} ($(phase_name "$PHASE")) - read only. Edits outside .claude/ are blocked. Complete this phase's work in chat, then run: bash .claude/hooks/advance.sh$([[ "$PHASE" == "1" ]] && printf ' "<trivial|standard>"')"
fi

exit 0
