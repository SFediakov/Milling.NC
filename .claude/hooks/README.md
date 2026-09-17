# .claude/hooks

Enforcement layer for the development process defined in the **root CLAUDE.md**.
The document is the specification; these scripts are the mechanism. When the two
disagree, the document wins and the scripts are wrong.

Everything here is `jq`-free on purpose: hooks run under Git Bash on Windows,
where `jq` is not installed.

## The phase model

Sub-agents are gated on the model, on a session-wide concurrency limit and, in
phase 2, on a per-half budget (see "Sub-agents" below); the phase itself never
forbids a spawn, so there is no Subagents column.

| # | Phase | Edits | Shell | Stop |
|---|-------|-------|-------|------|
| 1 | Task definition | read only | read only | blocks |
| 2 | Research block: files pre-research + WEB research, concurrent | read only | read only | blocks |
| 3 | retired number (WEB research is half of phase 2) | read only | read only | blocks |
| 4 | Planning | read only | read only | **may end turn** |
| 5 | Execution | free | free | blocks |
| 6 | Testing | free | free | **may end turn** |
| 7 | Documentation | free | free | blocks |
| 8 | Reporting | read only | **blocked** | blocks |
| 9 | Done / post-execution | free | free | allowed |

"Read only" means edits outside `.claude/` are blocked from the Edit/Write tools
**and** from the shell (redirection, `sed -i`, `cp`/`mv`/`touch`, repo-changing
git). The session scratchpad stays writable at every phase.

Phase 9 is not a CLAUDE.md phase. It is the terminal resting state that lets the
Stop gate tell "reporting still owed" from "task finished".

Phase 2 is a fork/join (owner decision, 2026-09-17): the document's phases 2
and 3 run at the same time under the number 2. Each half is marked finished
with its own call, in either order, and the second mark joins the block into
phase 4. The marks are two existence flags, `research_files.done` and
`research_web.done`, written by `advance.sh` only and locked against the agent;
one file per half so that the two marks never share a write target and need no
lock, and a repeated mark is refused so a flag can never count twice. The
number 3 is never produced by `advance.sh`; a hand-edited 3 is unreachable for
every class and is reported, not moved.

Phases 4 and 6 may end the turn early because their CLAUDE.md text explicitly
involves asking the user something - a blocking planning question, and approval
to modify an existing test. Neither is a routine pause.

## The task class

CLAUDE.md marks phases 2, 3 and 4 "skip for trivial tasks like PR creation". All
three carry the identical condition, so the decision is made once, when leaving
phase 1, and recorded in `.claude/state/task_class`:

```
trivial     1 -> 5   (2, 3, 4 skipped)
standard    1 -> 2   (nothing skipped)
```

Exactly those two strings, case-sensitive, no paraphrase. The hook applies the
skip table; the agent does not get to decide mid-task which phases exist.

## Commands

```bash
bash .claude/hooks/advance.sh              # phases 4-8
bash .claude/hooks/advance.sh "trivial"    # phase 1 only
bash .claude/hooks/advance.sh "standard"   # phase 1 only
bash .claude/hooks/advance.sh "files"      # phase 2 only: files pre-research finished
bash .claude/hooks/advance.sh "web"        # phase 2 only: WEB research finished
bash .claude/hooks/rollback.sh             # 6 -> 1, task class and marks cleared
```

A bare advance at phase 2 is refused and prints which halves are marked.

`rollback.sh` is the only backward transition, and it is legal only from phase 6
(CLAUDE.md: a failed test returns to task definition).

## Sub-agents

Root CLAUDE.md: "only opus sub-agents are allowed", "default max quantity of
agents is 8", and for WEB research "max quantity of agents for this phase is
3". Owner decision (2026-09-17): the limits are enforced, in this order, by
`gate-subagent.sh` (PreToolUse on `Agent`, the only sub-agent event that can
deny):

1. **Model.** The payload must carry **exactly one** `"model"` field equal to
   `SUBAGENT_ALLOWED_MODEL` (`opus`). Any other value, an omitted model, or a
   duplicated field is denied. Every occurrence is collected and counted, so a
   decoy ahead of the real one buys nothing; an omitted model inherits the
   session model, which a hook cannot see.
2. **Research block (task mode, phase 2).** The `description` must start with
   `files:` or `web:`, naming the half the agent works for - no hook field
   carries the phase half, so the tag is the only attribution possible. Each
   half has a spawn budget for the whole block (`RESEARCH_FILES_MAX` = 5,
   `RESEARCH_WEB_MAX` = 3), a *quantity* in the CLAUDE.md sense: finished
   agents free their concurrency slot, not their budget unit. The budget is
   cleared when the block is entered and when it is left.
3. **Concurrency.** At most `SUBAGENT_MAX_CONCURRENT` (8) sub-agents run at the
   same time, at every phase, task mode or not. The 9th is denied at once - the
   gate never waits, because a PreToolUse hook that times out lets the call
   through.

A running sub-agent still never blocks `advance.sh`, `rollback.sh` or the end
of a turn.

### The ledger

`.claude/state/agents/`, flat, one file per fact, locked against the agent by
`AGENT_LOCKED_STATE_REGEX` (an agent that could delete a slot file would lift
its own limit):

| File | Written by | Meaning |
|---|---|---|
| `slot.<k>` (k = 1..8) | `gate-subagent.sh` | held while a sub-agent occupies slot k |
| `res.<tool_use_id>` | `gate-subagent.sh` | which slot this Agent call reserved |
| `agent.<agent_id>` | `track-agent-result.sh` | slot + reservation of a running agent |
| `budget.<half>.<n>` | `gate-subagent.sh` | one admitted research spawn |

Slots are taken with an **exclusive create** (bash `noclobber`, O_EXCL), the
one primitive verified atomic on NTFS/Cygwin and Linux, so a burst of parallel
Agent calls - Claude Code runs their hooks concurrently - admits exactly 8 and
denies the rest (pinned by the test suite). `mv` is *not* a compare-and-swap on
Cygwin (two renames of one source can both succeed), so it is used only to free
a slot name before deleting it, never to decide anything.

Lifecycle, as observed live on 2026-09-18:

- `PreToolUse(Agent)` reserves `slot.k` under the call's `tool_use_id`.
- `PostToolUse(Agent)` (`track-agent-result.sh`) reads `tool_response`. Status
  `async_launched` (every spawn in fork mode) binds the reservation to
  `tool_response.agentId`, which equals the `agent_id` of SubagentStart/Stop.
  Any other status releases at once: for a foreground run the tool returns
  after the agent finished, and its SubagentStop has *already* fired and found
  no link. `PostToolUseFailure` releases too.
- `SubagentStop` (`track-subagent-stop.sh`) releases through the link. An agent
  without a link (Workflow agent, Skill fork, resumed run) is logged, nothing
  else. This hook exits 0 on every path: a "block" here would keep the agent
  running, not cancel it.
- `TaskStop` fires **no** SubagentStop. The harness's `<task-notification>`
  (`status: killed`) is the one signal that arrives, and its `<task-id>` is the
  agent id; `inject-phases.sh` settles the ledger from it, by agent id or, when
  PostToolUse never ran, by `<tool-use-id>`.
- `SubagentStart` (`track-subagent-start.sh`) is audit only: it cannot block
  and takes no slot. Workflow agents report here as `agent_type:
  workflow-subagent`.
- A reservation never bound to an agent is reclaimed after
  `LEDGER_RESERVATION_TTL_MIN` (5) minutes. A bound slot never expires by age:
  an agent may run for an hour, and over-admitting is the wrong direction.
- `session-start.sh` deletes the ledger on every process restart (startup,
  clear, resume) and keeps it on compact and on a re-fire for the same session
  id, where background agents are still running.

`.claude/state/subagent_events.log` (bounded, 200 lines) records every
reserve/link/start/stop/settle with the running count; it is the first thing to
read when a spawn is denied unexpectedly. A slot that is genuinely stuck (an
agent lost without any of the signals above) is cleared by a session restart or
by the user with a shell of their own (delete `.claude/state/agents`) - the
agent's tools are denied that path on purpose.

### What the gate cannot see

- The **Workflow tool**, the **Skill tool** (`context: fork`) and **MCP**
  servers spawn without a `PreToolUse(Agent)`, so neither the 8-slot limit nor
  the phase-2 tags and budgets apply to them at hook level. Their agents are
  *counted* only in the audit log, never in the slots.
- For those surfaces `settings.json` sets the runtime's own knobs, as a second
  layer in the sense of `gate-claude-md.sh` (same rule, independent
  mechanism), not as a fallback: `CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS=8`
  (Agent tool; documented exemptions: ultracode sessions, resumes, `/subtask`)
  and `CLAUDE_CODE_WORKFLOW_MAX_CONCURRENT_AGENTS=8`. The latter was verified
  live: 12 workflow agents launched at once, exactly 8 started in the first
  second, the 9th only after the first stop (this machine's CPU-derived default
  is 14). No documented setting splits a limit per phase or per agent type, so
  the 5/3 budgets exist only for Agent-tool spawns.
- A resumed sub-agent (`SendMessage` to a finished or killed agent) starts
  again under the same id without a `PreToolUse(Agent)`: it runs without a
  slot, and its stop is a no-op on the ledger.

## Phase model version

`.claude/state/phase_model` records which phase model the stored number belongs
to; the current value is **3**.

A bare number is ambiguous across models - 7 meant *Execution* under the retired
11-phase model and means *Documentation* here; 3 meant *WEB research* under
model 2 and is not a phase under model 3 - and `current_phase` survives resume,
compact and hand-editing. So:

- `session-start.sh` refuses to preserve a phase carrying a stale stamp and
  resets to 1 instead. This overrides every other preserve signal.
- `advance.sh` and `rollback.sh` normalise a stale or unstamped phase back to 1
  and refuse the transition, loudly.
- `read_phase` clamps any number outside `[1, 9]` into range, so a leftover 10 or
  11 reads as the terminal phase rather than as a phase nobody defines.

There is deliberately **no translation table** between models. Guessing what an
old number meant is the exact silent misinterpretation the stamp exists to stop.

Bump `PHASE_MODEL_VERSION` in `lib/guard-common.sh` whenever the meaning of a
phase number changes, and update the two duplicated copies (below).

## Files

| File | Event | Role |
|---|---|---|
| `lib/guard-common.sh` | - | Single source of truth: phase model, skip table, research-block join flags, sub-agent limits and ledger helpers, protected paths, shell write detection |
| `advance.sh` | - | Forward transitions; task-class gate at phase 1; research-block marks and join at phase 2 |
| `rollback.sh` | - | 6 -> 1, the only backward transition; clears marks and budgets |
| `session-start.sh` | SessionStart | Injects the rules; decides reset vs preserve |
| `inject-phases.sh` | UserPromptSubmit | Phase-9 exit rule, `?` discriminator, consent grant, killed-agent ledger release, audit log |
| `gate-edit.sh` | PreToolUse Edit/Write/NotebookEdit | Protected paths, root CLAUDE.md, phase state, read-only phases |
| `gate-claude-md.sh` | PreToolUse Edit/Write/NotebookEdit | Second, independent layer on the root CLAUDE.md |
| `gate-bash.sh` | PreToolUse Bash/PowerShell | Destructive commands, protected assets, remote main is PR-only (see below), read-only phases, phase-8 shell block |
| `gate-pr-base.sh` | PreToolUse Bash/PowerShell | `gh pr create --base main`; blocks merge/approve |
| `gate-subagent.sh` | PreToolUse Agent | Model (exactly one `"model":"opus"`), phase-2 half tag and budget, 8-slot concurrency reservation |
| `track-agent-result.sh` | PostToolUse / PostToolUseFailure Agent | Binds the reservation to the agent id (`async_launched`) or releases it |
| `track-subagent-start.sh` | SubagentStart | Audit log only |
| `track-subagent-stop.sh` | SubagentStop | Releases the finished agent's slot; never blocks |
| `gate-mcp-write.sh` | PreToolUse mcp__.* | Denies file-writing MCP tools |
| `gate-stop.sh` | Stop | Terminal-phase requirement, rebuild latch |
| `post-edit.sh` | PostToolUse | Edit log, CalcEngine rebuild latch |
| `tests/hook_tests.sh` | - | Regression suite; runs every hook in a throwaway project dir |

### Remote main is PR-only

Owner decision (2026-09-14): local main must never reach GitHub except through
a PR the user merges, and the limitation is hardcoded in `gate-bash.sh`, at
every phase, task mode or not. `gh pr merge`, `gh pr review --approve` and the
`gh api .../merge` route were already denied by `gate-pr-base.sh`; the push
side was open.

The rule is deliberately static, because the hook runs in a sandbox during
tests and must never depend on the working tree's state. Consequences:

- A push must show a named non-main branch as its target on the command line.
  `git push origin <branch>` passes; `git push origin <branch>:<branch>` too.
- Bare `git push`, `git push origin`, `git push -u origin HEAD` are denied
  even from a feature branch: git would resolve the target from
  `push.default` and the upstream, and the hook cannot read that. The deny
  reason names the accepted form.
- `--all`, `--mirror`, `--branches` and any glob refspec are denied because
  they sweep main in.
- Pushing local main to a non-main remote branch (`main:claude/backup`) is
  allowed: it does not change remote main.
- Local main is written only by a fast-forward sync (`git pull --ff-only
  origin main`, `git fetch origin main:main`). Explicit rewrites are denied:
  a fetch/pull refspec into main from anything else, `branch -f/-M/-m/-C/-d
  main`, `update-ref` on main, `checkout -B main`, `switch -C main`.
- `git merge <feature>` or `git pull origin <feature>` while main happens to be
  checked out is NOT caught (no branch lookup by design). Such a local main
  cannot leave the machine through the agent because every push route to
  remote main is denied.

The parser (`git_verb_args`) tokenizes with `read -ra`, never an unquoted
expansion, so a glob refspec is not expanded against the working directory;
it strips quote characters so PowerShell quoting cannot hide a token; and it
skips git's own global options (`-C <dir>`, `-c <k=v>`, `--git-dir=...`) so
`git -C x push origin main` is still read as a push.

Lesson learned while writing it: `mapfile -t arr < <(fn)` tests mapfile's
status, not `fn`'s, so a "not this verb" return was silently lost. The helper
`git_args_into` captures the function's status through an intermediate string.
Second lesson: a probe command whose text contains `git push origin main` is
itself denied by the live gate, so probes belong in a script file in the
scratchpad, or in `tests/hook_tests.sh`.

### Duplicated constants

`session-start.sh` and `inject-phases.sh` deliberately do **not** source
`lib/guard-common.sh`. Every guard that sources it fails closed, which is right
for a permission gate but wrong for the session and prompt paths: a library that
will not parse would then also swallow the rules text and the user's prompt
context.

The cost is two duplicated constants, `PHASE_DONE` and `PHASE_MODEL_VERSION`.
`tests/hook_tests.sh` asserts they equal the library's values, so they cannot
drift silently.

## Editing `lib/guard-common.sh`

**Never edit it by line offset, splice, or any operation whose result is not
known in full before it lands.**

Every guard sources this file and fails closed. A file that does not parse denies
Bash, PowerShell, Edit, Write **and** Agent at the same moment, and there is then
no route back from inside the session - the agent cannot repair the very file
that is blocking it. Only the user can, from an external terminal.

Safe methods, in order of preference:

1. Write the whole finished file in one operation.
2. Write a copy, `bash -n` it, and only then move it into place.

Recovery, if it happens anyway:

```bash
git checkout -- .claude/hooks/lib/guard-common.sh
bash -n .claude/hooks/lib/guard-common.sh && echo "SYNTAX OK"
```

## Consent to modify the enforcement layer

`.claude/settings.json` and everything under `.claude/hooks/` are denied to
Edit, Write and the shell unless `.claude/state/hook_edit_grant` exists.

Claude asks, in chat:

> if AI allowed to modify claude settings?

The grant is created when the user's prompt contains this phrase, byte for byte
and case-sensitively:

```
yes claude allowed to modify claude settings
```

It is a substring match by explicit owner decision, so the phrase can be written
inline with the request it authorises. The rest of the prompt is still processed
normally - an inline grant does not stop the phase rules applying to the request
that carries it.

Sharing the phrase (owner decision, 2026-09-17): Claude does not quote it on its
own initiative - it points at this file instead, because a quoted phrase pasted
back would grant by accident. When the user asks for the phrase, Claude quotes
it: a direct request outranks that caution.

The grant lasts **one turn**. `gate-stop.sh` deletes it on every path where the
turn is allowed to end, and keeps it when the Stop gate blocks, because a
blocked stop means the agent is still working. `session-start.sh` clears it
unconditionally, resume and compact included.

Why this works when an approval prompt did not: `hook_edit_grant` is in
`AGENT_LOCKED_STATE_REGEX`, the same list as `TASK_MODE` and `current_phase`,
which is the one mechanism verified to hold against Edit, Write, the shell, path
traversal and case variation. `inject-phases.sh` is its only writer.

Two rules that follow from the substring match, both enforced in code:

- The guards never print the passphrase in a deny reason, so pasting a hook
  error back into chat cannot create consent.
- Harness-generated turns (task notifications, CI events) are filtered out
  before the check runs, so a notification carrying the text cannot grant.

## Running the tests

```bash
bash .claude/hooks/tests/hook_tests.sh
```

The suite copies the hooks into a throwaway `CLAUDE_PROJECT_DIR` under `mktemp`,
so the live `.claude/state` is never touched. It exits non-zero if any assertion
fails. It is slow on Windows - several hundred subprocess spawns - so allow a few
minutes.

Run it after **any** change in this folder, and before swapping a new hook set
into place.

## Recovery

`current_phase`, `task_class` and `TASK_MODE` are not writable by the agent, and
never should be: the state machine is only real if the agent cannot move itself
through it. Only the user edits those, outside Claude Code.

Task mode toggle (user only):

```bash
touch .claude/state/TASK_MODE     # enable phase enforcement
rm .claude/state/TASK_MODE        # disable
```

With task mode off, the phase gates and the Stop gate do not run at all. The
always-on guards - protected assets, root CLAUDE.md, destructive commands, git
safety, phase-state files - still apply.

## Lessons learned

These came out of an adversarial probe of the live gates. Each one was a real
allow that should have been a deny, confirmed by running the actual hooks
against crafted payloads - not by reading the source.

**A guard that aborts must still emit a decision.** Every gate read
`${CLAUDE_PROJECT_DIR}` under `set -u`. Unset it, and the script died before
printing anything. A PreToolUse hook that exits non-zero with no JSON is read as
"no opinion" and the tool call proceeds. This is how a shell write to
`current_phase` succeeded during a `.claude` folder swap: the hook file was
momentarily absent, the invocation errored, nothing blocked. Every gate now
installs an EXIT trap that emits a deny on any non-zero exit, and checks
`CLAUDE_PROJECT_DIR` explicitly before using it. Fail-closed on *load* was
already right; fail-closed on *abort* was missing.

**Never whitelist by substring against a whole command line.** The
advance/rollback whitelist used `grep` over the raw `$CMD` and then `exit 0`,
skipping the read-only and phase-8 checks for the entire line. So
`dotnet build && bash .claude/hooks/advance.sh` ran at phase 8, and
`echo x > Server/pwn.cs && bash .claude/hooks/advance.sh` wrote a file in a
read-only phase. Putting the path in a comment worked equally well. Whitelists
are now evaluated per segment with all-segments-must-pass semantics.

**Normalize a path before matching it.** Backslash-to-slash conversion is not
normalization. `Server/../CLAUDE.md` reached the root document and
`.claude/state/../state/TASK_MODE` reached the master switch. `normalize_path`
resolves `.` and `..` textually - not via `realpath`, because a Write target
frequently does not exist yet.

**Match case-insensitively on a case-insensitive filesystem.** NTFS opens the
same file for `TASK_MODE` and `task_mode`, and for `.claude` and `.CLAUDE`.
`is_root_claude_md` had always lowercased; the phase-state and protected-asset
regexes had not, and both were bypassed by typing a different case.

**Scope an exemption to what it is for.** The scratchpad exemption matched all
of `AppData\Local\Temp`, so every temp file on the machine was writable during a
read-only phase. It now requires the `Temp/claude/` segment.

**A quote-aware redirect reader must know code from data.** Skipping quoted
spans is right for `grep 'a > b'` and wrong for `bash -c "echo x > f"`, which
escaped the read-only gate. Segments invoking an inline interpreter are now
re-scanned with quote characters removed.

**A killed sub-agent fires no SubagentStop.** Observed live: `TaskStop` on a
background agent produced no SubagentStop, so a slot released only there stays
held until the session restarts. The harness's `<task-notification>` with
`status: killed` is the signal that does arrive, and its `<task-id>` is the
agent id. Any per-agent accounting needs that path too, and needs the linking
key (`tool_use_id` -> `agentId`) to be established in PostToolUse before the
kill can happen.

**SubagentStop runs before PostToolUse for a foreground run.** The tool returns
after the agent has finished, so the stop hook fires first and finds no link.
Bind a reservation to an agent only on `status: async_launched`; treat every
other PostToolUse status as "already over" and release at once, or the slot
leaks for the session.

**`mv` is not a compare-and-swap on Git Bash.** Cygwin's rename is
handle-based: two processes renaming the same source can both report success.
Exclusive create (`set -C` + `printf >`) is the primitive that has exactly one
winner; use `printf`, not `:`, because a failed redirection on the special
builtin `:` terminates a POSIX-mode shell.

**A heredoc body is command text to `gate-bash.sh`.** Writing a hook that
contains `rm -rf "$1"` through `cat > file <<'EOF'` is scanned as if the shell
were about to run it, and denied. Write hook files with the Write tool, or
write a script file to the scratchpad and run that - the same rule the README
already gives for probes.

**A counter incremented inside `$(...)` is lost.** A test helper that mints
ids with `SEQ=$((SEQ + 1))` and is called as `"$(json_agent ...)"` runs in a
subshell, so every call sees the same number. Mint ids from a clock or a
random source instead.

**`permissionDecision: "ask"` did not gate anything in the observed
configuration.** `gate-claude-md.sh` asserts that an ask requires a user button
press even under `defaultMode: bypassPermissions`. Probed here, hook writes that
returned `ask` completed with no approval step. Treat `ask` as advisory until
re-verified on the client you actually use; do not build a protection on it
alone. The mechanism that *is* enforced is a deny keyed on a user-owned state
file, which is how `TASK_MODE` already works.

## What this layer is not

`gate-bash.sh` is a tripwire, not a security boundary. A regex denylist cannot
survive a determined bypass, and the following are known to be out of reach:

- **An interpreter handed a script file.** `bash writer.sh` is opaque to the
  guard; only inline payloads (`-c "..."`) are scanned.
- **Symlinks, hardlinks, 8.3 short names, NTFS alternate data streams.** These
  need filesystem-level controls, not string matching.
- **PATH and alias indirection.** Matching a command name says nothing about
  what that name resolves to at exec time.
- **Tools with no matcher.** `Artifact`, `Skill` and `Workflow` are wired to no
  PreToolUse hook; `Artifact` can write local files via `out_dir`, and a skill
  or a workflow can run a subagent without passing `gate-subagent.sh`, so the
  opus-only rule, the 8-slot limit and the phase-2 budgets are not verified on
  those surfaces - only the runtime's own `env` caps apply there (see
  "Sub-agents").
- **The enforcement layer itself.** The agent and the hooks share a uid, so
  under discretionary access control there is no permission that lets one edit
  its work but not its guardrails. Shell writes to `settings.json` and
  `.claude/hooks/**` are denied, and Edit/Write returns an ask - but see the
  lesson above about `ask`. A hard gate requires a user-owned marker file, or
  moving enforcement out of process entirely.

It exists to stop accidents, which is the actual failure mode.
