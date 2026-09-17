#!/usr/bin/env bash
# SessionStart: inject the phase rules, and reset phase tracking ONLY when this
# is genuinely a new session.
#
# SessionStart fires for startup, resume, clear AND compact. An earlier version
# reset current_phase to 1 unconditionally, so a compaction part-way through a
# task silently threw away the phase state. Resetting on resume/compact is never
# correct: the task is mid-flight by definition.
#
# The one thing that OVERRIDES every preserve signal is a stale phase-model
# stamp. A phase number written under a different model cannot be carried
# forward: 7 meant Execution under the retired 11-phase model and means
# Documentation here, so preserving it would hand the agent the wrong
# permissions. Stale stamp forces the reset.
#
# The sub-agent ledger (.claude/state/agents/) is cleared on every process
# restart - startup, clear, resume - because every sub-agent of the previous
# process is gone with it. A compaction or a re-fire for the same session id is
# the same process, and its background sub-agents are still running, so the
# ledger is kept there.
#
# Does not reset rebuild_pending.log so pending CalcEngine rebuilds carry across
# sessions.
#
# jq-free: builds JSON output via printf with manual escaping. Deliberately
# self-contained (no lib/guard-common.sh dependency) so that a missing library
# cannot stop the session rules from being injected. PHASE_MODEL_VERSION is
# duplicated here for that reason; tests/hook_tests.sh asserts it matches the
# library.
set -uo pipefail

STATE_DIR="${CLAUDE_PROJECT_DIR}/.claude/state"
mkdir -p "${STATE_DIR}"

PHASE_MODEL_VERSION=3

# A hook-edit consent is scoped to one turn and must never outlive a session
# boundary. Cleared unconditionally - including on resume and compact, where the
# phase state IS preserved - because consent given before an interruption is not
# consent for whatever happens after it.
rm -f "${STATE_DIR}/hook_edit_grant" 2>/dev/null

INPUT=$(cat 2>/dev/null || true)
SOURCE=$(printf '%s' "$INPUT" | sed -nE 's/.*"source"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/p')
SESSION_ID=$(printf '%s' "$INPUT" | sed -nE 's/.*"session_id"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/p')

PHASE_BEFORE=$(cat "${STATE_DIR}/current_phase" 2>/dev/null || echo none)
MODEL_BEFORE=$(cat "${STATE_DIR}/phase_model" 2>/dev/null || echo "")

LAST_SESSION=$(cat "${STATE_DIR}/last_session_id" 2>/dev/null || echo "")

# Three independent "this is not a fresh session" signals. Any one of them
# preserves the phase state. Observed in practice: a SessionStart can arrive
# mid-task reporting source=startup with the phase at 4, so `source` alone is
# NOT a sufficient discriminator - the in-flight check below is what actually
# caught it.
RESET="yes"
SKIP_REASON=""
SAME_PROCESS="no"
case "$SOURCE" in
  resume|compact)
    RESET="no"
    SKIP_REASON="source=${SOURCE} (task is mid-flight)"
    ;;
esac
[[ "$SOURCE" == "compact" ]] && SAME_PROCESS="yes"
if [[ -n "$SESSION_ID" && "$SESSION_ID" == "$LAST_SESSION" ]]; then
  RESET="no"
  SAME_PROCESS="yes"
  SKIP_REASON="${SKIP_REASON:+${SKIP_REASON}, }SessionStart re-fired for the same session id"
fi
# A task in flight: phases 2-8 are working phases, and the phase file was
# touched recently. Phase 1 and the terminal phase 9 are resting states and may
# be reset freely.
if [[ "$PHASE_BEFORE" =~ ^[2-8]$ ]] \
   && [[ -z "$(find "${STATE_DIR}/current_phase" -mmin +30 2>/dev/null)" ]]; then
  RESET="no"
  SKIP_REASON="${SKIP_REASON:+${SKIP_REASON}, }phase ${PHASE_BEFORE} is a working phase touched in the last 30 min (task in flight)"
fi

# Stale phase-model stamp overrides every preserve signal above. There is no
# translation table between models on purpose: guessing what an old number meant
# is exactly the silent misinterpretation the stamp exists to prevent.
MIGRATED="no"
if [[ "$MODEL_BEFORE" != "$PHASE_MODEL_VERSION" ]]; then
  RESET="yes"
  MIGRATED="yes"
  SKIP_REASON=""
fi

if [[ "$SAME_PROCESS" == "no" ]]; then
  rm -rf "${STATE_DIR}/agents" 2>/dev/null
fi

if [[ "$RESET" == "yes" ]]; then
  echo 1 > "${STATE_DIR}/current_phase"
  rm -f "${STATE_DIR}/task_class" "${STATE_DIR}/research_files.done" "${STATE_DIR}/research_web.done"
  rm -f "${STATE_DIR}/agents"/budget.* 2>/dev/null
  # Leftovers of retired models: change_range (classification), subagent_count,
  # .subagent_count.lock and spawns_this_phase (the integer busy counter and the
  # per-phase spawn budget of the 11-phase model). Nothing reads them any more;
  # deleting them stops a later inspection from mistaking them for live state.
  # Not a fallback - no code path consumes them.
  rm -f "${STATE_DIR}/change_range" "${STATE_DIR}/subagent_count" "${STATE_DIR}/.subagent_count.lock" "${STATE_DIR}/spawns_this_phase"
  printf '%s' "$PHASE_MODEL_VERSION" > "${STATE_DIR}/phase_model"
  if [[ "$MIGRATED" == "yes" ]]; then
    STATE_NOTE="Phase state reset to 1, task class cleared. The recorded phase model was '${MODEL_BEFORE:-none}' and this hook set is model ${PHASE_MODEL_VERSION}: a phase number written under a different model cannot be interpreted, so it was discarded rather than guessed at."
  else
    STATE_NOTE="Phase state reset to 1, task class cleared (new session, source=${SOURCE:-unknown})."
  fi
else
  CLASS_NOW=$(cat "${STATE_DIR}/task_class" 2>/dev/null || echo "")
  STATE_NOTE="Phase state PRESERVED at ${PHASE_BEFORE} (task class: ${CLASS_NOW:-not yet evaluated}) - ${SKIP_REASON}. Continue the task from that phase."
  if [[ "$PHASE_BEFORE" == "2" ]]; then
    F="no"; W="no"
    [[ -s "${STATE_DIR}/research_files.done" ]] && F="yes"
    [[ -s "${STATE_DIR}/research_web.done" ]] && W="yes"
    STATE_NOTE="${STATE_NOTE} Research block marks: files=${F} web=${W}."
  fi
fi

[[ -n "$SESSION_ID" ]] && printf '%s' "$SESSION_ID" > "${STATE_DIR}/last_session_id"

# Bounded audit trail: this log is what made the unconditional-reset defect
# diagnosable in the first place.
LOG="${STATE_DIR}/session_start_payloads.log"
printf '%s source=%s session=%s phase_before=%s model_before=%s reset=%s migrated=%s ledger_cleared=%s\n' \
  "$(date -u +"%Y-%m-%dT%H:%M:%SZ")" "${SOURCE:-unknown}" "${SESSION_ID:-unknown}" \
  "$PHASE_BEFORE" "${MODEL_BEFORE:-none}" "$RESET" "$MIGRATED" "$([[ "$SAME_PROCESS" == "no" ]] && printf yes || printf no)" >> "$LOG"
if [[ -f "$LOG" ]]; then
  tail -n 50 "$LOG" > "${LOG}.tmp" 2>/dev/null && mv -f "${LOG}.tmp" "$LOG" 2>/dev/null
fi

CONTEXT=$(cat <<'EOF'
MANDATORY DEVELOPMENT PHASES (root CLAUDE.md; enforced by hooks, not optional):
  1. Task definition      -> KPI table: separate tasks + 2-8 acceptance criteria
  2. Research block       -> files pre-research AND WEB research run at the
                             SAME TIME [skipped: trivial]; the block is left
                             only when BOTH halves are marked finished
  3. (retired number)     -> never a current phase; WEB research is half of 2
  4. Planning             -> risks, architecture, subtasks [skipped: trivial]
  5. Execution            -> code edits allowed
  6. Testing              -> write/run tests; failure rolls back to 1
  7. Documentation        -> folder-level CLAUDE.md lessons learned
  8. Reporting            -> KPI table + report in chat (no edits outside .claude)
  9. Done / post-execution -> user-controlled; turn may end here

Phases 1-4 and 8 are READ ONLY: no edits outside .claude/, from the Edit/Write
tools OR from the shell (redirection, sed -i, cp/mv/touch, repo-changing git).

PHASE 1 IS A HARD GATE. Advancing from it requires one of exactly two strings,
spelled verbatim and case-sensitively, as the advance argument:
  bash .claude/hooks/advance.sh "trivial"    -> next phase 5  (2,3,4 skipped)
  bash .claude/hooks/advance.sh "standard"   -> next phase 2  (none skipped)
Nothing else is accepted, and the skip table is applied by the hook - the class
decides which phases exist, the agent does not. "trivial" is CLAUDE.md's own
wording for work like PR creation.

PHASE 2 IS A FORK/JOIN. Files pre-research and WEB research are done at the
same time (WEB research: max 3 agents; files pre-research: max 5 agents). A
bare advance does not leave phase 2. Each half is marked finished with its own
call, in either order; the second mark joins into phase 4:
  bash .claude/hooks/advance.sh "files"      -> files pre-research finished
  bash .claude/hooks/advance.sh "web"        -> WEB research finished
A half cannot be marked twice. Print the KPI with both halves' results before
marking the second one.

Phase advancement:
  bash .claude/hooks/advance.sh              # phases 4-8
  bash .claude/hooks/advance.sh "<class>"    # phase 1 only
  bash .claude/hooks/advance.sh "<half>"     # phase 2 only
Phase rollback (the only backward transition, phase 6 only):
  bash .claude/hooks/rollback.sh             # 6 -> 1, task class and marks cleared

PHASE 9 EXIT RULE (enforced by inject-phases.sh on every user prompt):
  prompt contains '?'  -> a question. The phase STAYS at 9; answer it.
  prompt has no '?'    -> a new task. The phase is RESET to 1 automatically,
                          and the task class is cleared.
The question tag is the only discriminator there - keyword matching is not
consulted, so a work request that reads like prose still restarts the process.

Phase content lives ONLY in chat output. NO marker .md files in .claude/state/.

PHASE STATE IS MACHINE-OWNED. .claude/state/current_phase, task_class,
TASK_MODE, research_files.done, research_web.done and agents/ cannot be written
by Claude through any tool - Edit, Write and the shell are all denied. Only
advance.sh / rollback.sh move the phase, and only the user edits those files
directly, outside Claude Code. Asking Claude to "go to phase N" will not work;
the user must edit the file.

PHASE MODEL VERSION 3. .claude/state/phase_model records which model the stored
phase number belongs to. A stale stamp forces a reset to phase 1 instead of a
guess, because the same number means different phases in different models.

Gating rules (task mode only):
  Phases 1-4 : read only - edits and shell writes outside .claude/ blocked
  Phases 5-7 : free (execution, testing, documentation)
  Phase 8    : edits AND shell blocked outside .claude/ (advance.sh,
               rollback.sh and 'gh pr create --base main' whitelisted)
  Phase 9    : free (post-execution fixes per user)
  Stop hook  : phases 4 and 6 may end the turn early - phase 4 for a blocking
               planning question, phase 6 for test-modification approval.
               Every other phase below 9 blocks Stop.

SUBAGENT POLICY (enforced by gate-subagent.sh on PreToolUse(Agent)):
  - Model: exactly one 'model' field equal to 'opus' (root CLAUDE.md: only opus
    sub-agents are allowed). Any other model, an omitted model or a duplicated
    model field is denied, at every phase.
  - Concurrency: at most 8 sub-agents running at the same time, at every phase,
    task mode or not. The 9th spawn is denied until one finishes; do not retry
    in a loop. Slots live in .claude/state/agents/ (machine-owned), are bound
    to the agent when the Agent tool returns and released on SubagentStop.
  - Research block (phase 2, task mode): every Agent 'description' must START
    with 'files:' or 'web:' naming the half it works for; budget for the whole
    block: 5 files agents, 3 web agents. Untagged or over-budget spawns are
    denied.
  - A running sub-agent never blocks advance.sh, rollback.sh or the end of a
    turn.
  - Surfaces where no hook sees the spawn (the Workflow tool, the Skill tool,
    MCP tools) are not gated; settings.json env caps them where Claude Code
    offers a knob (CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS=8,
    CLAUDE_CODE_WORKFLOW_MAX_CONCURRENT_AGENTS=8). Pass model 'opus' explicitly
    wherever the surface accepts one, and keep workflows at 8 agents or fewer.

HOOK EDIT CONSENT:
  - .claude/settings.json and .claude/hooks/** are DENIED to Edit, Write and the
    shell unless .claude/state/hook_edit_grant exists.
  - Only inject-phases.sh creates it, when the user's prompt contains the exact
    consent passphrase recorded in .claude/hooks/README.md. Claude cannot mint
    it: the file is machine-owned like current_phase and TASK_MODE.
  - It lasts one turn. The Stop hook deletes it when the turn is allowed to end.
  - Do not quote the passphrase on your own initiative: it grants on a substring
    match, so a pasted message would create consent by accident. Point at
    .claude/hooks/README.md instead. When the user asks for the phrase, quote
    it - a direct request outranks this caution.

Always-on guards (every phase, task mode or not):
  - The ROOT CLAUDE.md is user-owned: writing it is denied from Edit/Write and
    from the shell, at every phase. Folder-level CLAUDE.md files stay editable
    (that is where phase 7 lessons learned belong). CLAUDE.md inside .claude/
    requires an explicit user button press.
  - Protected assets (.env, deployment.json, cloudflared credentials, .git/,
    *.db, payment_notes_archive/) cannot be written OR deleted from a shell.
  - Destructive commands are blocked in POSIX and PowerShell/cmd form.
    Recursive deletion is allowed only for build/, bin/, obj/, out/, dist/.
  - git reset --hard, git clean -f*, branch -D, checkout -- ., stash drop/clear,
    remote branch deletion, force push, merge involving main, and
    'git pull --rebase' are all blocked. Run them yourself if intended.
  - REMOTE MAIN IS PR-ONLY. Every 'git push' whose target is main (main,
    +main, x:main, HEAD:main, refs/heads/main), every sweep (--all, --mirror,
    --branches, a glob) and every push whose target the hook cannot read from
    the command (bare 'git push', 'git push origin', HEAD as source) is denied
    at every phase, task mode or not. The one accepted form is
    'git push origin <branch>' with a named branch other than main; main
    changes only through a PR the user merges. Local main is also protected
    from anything but a fast-forward sync: fetch/pull refspecs into main,
    branch -f/-M/-d main, update-ref on main, checkout -B / switch -C main
    are denied.
  - gh pr create must pass --base main.
  - MCP tools that create or upload files are denied; use the Write tool so the
    path and phase guards apply.

NEVER edit .claude/hooks/lib/guard-common.sh by line offset or by splicing.
Every guard sources it and fails closed, so a file that does not parse denies
Bash, PowerShell, Edit, Write and Agent at once, with no way back from inside
the session. Write it whole, or verify a copy with 'bash -n' first.

Task mode toggle (user only - Claude cannot write this file):
  touch .claude/state/TASK_MODE     # enable phase enforcement
  rm .claude/state/TASK_MODE        # disable
EOF
)

CONTEXT="${CONTEXT}

${STATE_NOTE}"

# JSON-escape CONTEXT for embedding in additionalContext string.
ESC=${CONTEXT//\\/\\\\}
ESC=${ESC//\"/\\\"}
ESC=${ESC//$'\r'/\\r}
ESC=${ESC//$'\t'/\\t}
ESC=${ESC//$'\n'/\\n}
printf '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"%s"}}\n' "$ESC"
