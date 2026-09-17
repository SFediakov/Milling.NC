#!/usr/bin/env bash
# Test suite for the .claude/hooks guards.
#   bash .claude/hooks/tests/hook_tests.sh
#
# Runs every hook against crafted payloads inside a throwaway CLAUDE_PROJECT_DIR
# so the live session state in .claude/state is never touched. Exits non-zero on
# the first failing assertion count.
#
# Phase numbering follows the root CLAUDE.md: 1 task definition,
# 2 files pre-research, 3 WEB research, 4 planning, 5 execution, 6 testing,
# 7 documentation, 8 reporting, 9 done (terminal, hook-only).
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
HOOK_SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SANDBOX="$(mktemp -d)"
trap 'rm -rf "$SANDBOX"' EXIT

mkdir -p "$SANDBOX/.claude"
cp -r "$HOOK_SRC" "$SANDBOX/.claude/hooks"
mkdir -p "$SANDBOX/.claude/state"
export CLAUDE_PROJECT_DIR="$SANDBOX"

HOOKS="$SANDBOX/.claude/hooks"
STATE="$SANDBOX/.claude/state"

# The model version the hooks under test declare. Read from the library so this
# suite cannot silently test a stale number.
MODEL_VERSION=$(bash -c "source '$HOOKS/lib/guard-common.sh' && printf '%s' \"\$PHASE_MODEL_VERSION\"")
LIB_PHASE_DONE=$(bash -c "source '$HOOKS/lib/guard-common.sh' && printf '%s' \"\$PHASE_DONE\"")

PASS=0
FAIL=0

# set_phase also stamps the model: a phase number is only meaningful together
# with the model it belongs to, and every migration path keys off that stamp.
set_phase()      { echo "$1" > "$STATE/current_phase"; printf '%s' "$MODEL_VERSION" > "$STATE/phase_model"; }
set_model()      { printf '%s' "$1" > "$STATE/phase_model"; }
set_class()      { printf '%s' "$1" > "$STATE/task_class"; }
clear_class()    { rm -f "$STATE/task_class"; }
task_mode_on()   { : > "$STATE/TASK_MODE"; }
task_mode_off()  { rm -f "$STATE/TASK_MODE"; }
clear_spawns()   { rm -f "$STATE/spawns_this_phase"; echo 0 > "$STATE/subagent_count"; }

json_cmd()   { printf '{"tool_name":"Bash","tool_input":{"command":"%s"}}' "$1"; }
json_write() { printf '{"tool_name":"Write","tool_input":{"file_path":"%s","content":"x"}}' "$1"; }
json_agent() { printf '{"tool_name":"Agent","tool_input":{"model":"%s","prompt":"p"}}' "$1"; }
json_mcp()   { printf '{"tool_name":"%s","tool_input":{}}' "$1"; }

report() {
  local ok="$1" name="$2" detail="${3:-}"
  if [[ "$ok" == "1" ]]; then
    PASS=$((PASS + 1))
  else
    FAIL=$((FAIL + 1))
    printf '  FAIL: %s %s\n' "$name" "$detail"
  fi
}

expect_deny() {
  local out; out=$(printf '%s' "$2" | bash "$HOOKS/$1" 2>/dev/null)
  if printf '%s' "$out" | grep -q '"permissionDecision":"deny"'; then report 1 "$3"; else report 0 "$3" "(expected deny, got allow)"; fi
}
expect_allow() {
  local out; out=$(printf '%s' "$2" | bash "$HOOKS/$1" 2>/dev/null)
  if printf '%s' "$out" | grep -q '"permissionDecision":"deny"'; then report 0 "$3" "(expected allow, got deny)"; else report 1 "$3"; fi
}
expect_ask() {
  local out; out=$(printf '%s' "$2" | bash "$HOOKS/$1" 2>/dev/null)
  if printf '%s' "$out" | grep -q '"permissionDecision":"ask"'; then report 1 "$3"; else report 0 "$3" "(expected ask)"; fi
}

# advance/rollback helpers: exit status is the assertion.
adv()      { bash "$HOOKS/advance.sh" "$@" >/dev/null 2>&1; }
rollback() { bash "$HOOKS/rollback.sh" "$@" >/dev/null 2>&1; }
phase_is() { [[ "$(cat "$STATE/current_phase")" == "$1" ]]; }

# Baseline: execution phase, standard class (nothing skipped).
task_mode_on; set_phase 5; set_class "standard"; clear_spawns

echo "== phase model is the single source of truth =="
[[ "$LIB_PHASE_DONE" == "9" ]] && report 1 "library PHASE_DONE is 9" || report 0 "library PHASE_DONE" "(got $LIB_PHASE_DONE)"
[[ "$MODEL_VERSION" == "2" ]] && report 1 "library PHASE_MODEL_VERSION is 2" || report 0 "library PHASE_MODEL_VERSION" "(got $MODEL_VERSION)"
( source "$HOOKS/lib/guard-common.sh"
  names="1:Task definition 2:Files pre-research 3:WEB research 4:Planning 5:Execution 6:Testing 7:Documentation 8:Reporting"
  ok=1
  for pair in $names; do :; done
  for n in 1 2 3 4 5 6 7 8; do
    case "$n" in
      1) want="Task definition" ;;
      2) want="Files pre-research" ;;
      3) want="WEB research" ;;
      4) want="Planning" ;;
      5) want="Execution" ;;
      6) want="Testing" ;;
      7) want="Documentation" ;;
      8) want="Reporting" ;;
    esac
    [[ "$(phase_name "$n")" == "$want" ]] || { printf 'PN_FAIL %s got[%s] want[%s]\n' "$n" "$(phase_name "$n")" "$want"; ok=0; }
  done
  [[ "$(phase_name 10)" == "unknown" ]] || { printf 'PN_FAIL 10 not unknown\n'; ok=0; }
  [[ "$ok" == "1" ]] && printf 'PN_OK\n'
) > "$SANDBOX/pn.out" 2>&1
grep -q PN_OK "$SANDBOX/pn.out" && report 1 "phase_name matches the CLAUDE.md phase list" || report 0 "phase_name list" "($(tr '\n' ' ' < "$SANDBOX/pn.out"))"

# The two self-contained hooks duplicate PHASE_DONE / PHASE_MODEL_VERSION on
# purpose (they must not fail closed on a broken library). Pin them so the
# duplicates cannot drift away from the library.
grep -q "^PHASE_DONE=${LIB_PHASE_DONE}$" "$HOOKS/inject-phases.sh" && report 1 "inject-phases PHASE_DONE matches the library" || report 0 "inject-phases PHASE_DONE drift"
grep -q "^PHASE_MODEL_VERSION=${MODEL_VERSION}$" "$HOOKS/inject-phases.sh" && report 1 "inject-phases model version matches the library" || report 0 "inject-phases model version drift"
grep -q "^PHASE_MODEL_VERSION=${MODEL_VERSION}$" "$HOOKS/session-start.sh" && report 1 "session-start model version matches the library" || report 0 "session-start model version drift"

echo "== read_phase clamps a number from a retired model =="
( source "$HOOKS/lib/guard-common.sh"
  echo 11 > "$STATE/current_phase"; printf 'C11=%s\n' "$(read_phase "$STATE")"
  echo 10 > "$STATE/current_phase"; printf 'C10=%s\n' "$(read_phase "$STATE")"
  echo 0  > "$STATE/current_phase"; printf 'C0=%s\n'  "$(read_phase "$STATE")"
  echo xx > "$STATE/current_phase"; printf 'CX=%s\n'  "$(read_phase "$STATE")"
) > "$SANDBOX/clamp.out" 2>&1
grep -q '^C11=9$' "$SANDBOX/clamp.out" && report 1 "retired phase 11 clamps to the terminal phase" || report 0 "clamp 11"
grep -q '^C10=9$' "$SANDBOX/clamp.out" && report 1 "retired phase 10 clamps to the terminal phase" || report 0 "clamp 10"
grep -q '^C0=1$'  "$SANDBOX/clamp.out" && report 1 "phase 0 clamps to the first phase"          || report 0 "clamp 0"
grep -q '^CX=1$'  "$SANDBOX/clamp.out" && report 1 "non-numeric phase falls back to the first"  || report 0 "clamp non-numeric"
set_phase 5

echo "== R1/R2: destructive commands must be blocked =="
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_deny gate-bash.sh "$(json_cmd "$c")" "block: $c"
done <<'CASES'
rm -rf .
rm -rf Server/native
rm -f Server/corner.db
rm .env
rm -rf orchistration/cloudflared
Remove-Item -Recurse -Force ./Server
rd /s /q Server
del /s /q Server
git reset --hard origin/main
git clean -xfd
git branch -D main
git checkout -- .
git stash drop
git push origin --delete feature
git push --force origin main
git merge origin/main
git pull --rebase
git rm -r Server
mkfs.ext4 /dev/sda
echo x > orchistration/deployment.json
cat foo > Server/corner.db
Format-Volume -DriveLetter C
CASES

echo "== R7: interpreters are scanned inside their quoted payload =="
expect_deny gate-bash.sh "$(json_cmd "bash -c 'rm -rf .'")"                              "block: bash -c payload"
expect_deny gate-bash.sh "$(json_cmd "powershell -Command 'Remove-Item -Recurse -Force ./Server'")" "block: powershell -Command payload"
expect_deny gate-bash.sh "$(json_cmd "sudo rm -rf /etc")"                                "block: sudo wrapper"
expect_deny gate-bash.sh "$(json_cmd "git commit -m ok && rm -rf Server")"               "block: destructive second segment"

echo "== R7: quoted data must NOT trigger a false positive =="
expect_allow gate-bash.sh "$(json_cmd "grep -rn 'rm -rf /' server_logs/")"               "allow: grep for the pattern"
expect_allow gate-bash.sh "$(json_cmd "git commit -m 'fix: drop rm -rf cleanup step'")"  "allow: commit message mentioning rm -rf"
expect_deny  gate-bash.sh "$(json_cmd "git commit -m \$(rm -rf .)")"                      "block: command substitution in a commit message"

echo "== R1: routine work must stay allowed (execution phase) =="
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_allow gate-bash.sh "$(json_cmd "$c")" "allow: $c"
done <<'CASES'
git push origin feature-branch
git commit -m message
git log --oneline -5
git status
git diff --stat
dotnet test Server.Tests/Server.Tests.csproj -c Release
cmake --build build
rm -rf build
rm -rf CalcEngine/native/build
rm Server/wwwroot/js/old.js
gh pr create --base main --title x
ls -la
grep -n WebAppVersion Server/Program.cs
sed -i s/a/b/ Server/wwwroot/js/app.js
echo x > Server/notes.txt
cp Server/a.cs Server/b.cs
CASES

echo "== the root CLAUDE.md is never writable =="
expect_deny gate-edit.sh     "$(json_write "$SANDBOX/CLAUDE.md")"          "gate-edit blocks root CLAUDE.md"
expect_deny gate-claude-md.sh "$(json_write "$SANDBOX/CLAUDE.md")"         "gate-claude-md blocks root CLAUDE.md"
expect_deny gate-bash.sh "$(json_cmd "echo x > CLAUDE.md")"                "shell redirect onto root CLAUDE.md"
expect_deny gate-bash.sh "$(json_cmd "echo x >> ./CLAUDE.md")"             "shell append onto root CLAUDE.md"
expect_deny gate-bash.sh "$(json_cmd "echo x > \\\"CLAUDE.md\\\"")"        "quoted redirect onto root CLAUDE.md"
expect_deny gate-bash.sh "$(json_cmd "sed -i s/a/b/ CLAUDE.md")"           "sed -i on root CLAUDE.md"
expect_deny gate-bash.sh "$(json_cmd "cp /tmp/x CLAUDE.md")"               "cp onto root CLAUDE.md"
expect_deny gate-bash.sh "$(json_cmd "echo x > $SANDBOX/CLAUDE.md")"       "absolute redirect onto root CLAUDE.md"
# Folder-level CLAUDE.md files stay editable - phase 7 requires maintaining them.
expect_allow gate-edit.sh "$(json_write "$SANDBOX/Watchdog/CLAUDE.md")"    "folder-level CLAUDE.md still writable"
expect_allow gate-bash.sh "$(json_cmd "echo x > Watchdog/CLAUDE.md")"      "folder-level CLAUDE.md writable from shell"
expect_allow gate-bash.sh "$(json_cmd "grep -n rule CLAUDE.md")"           "reading root CLAUDE.md stays allowed"
expect_allow gate-bash.sh "$(json_cmd "cat CLAUDE.md")"                    "cat root CLAUDE.md stays allowed"
expect_ask   gate-claude-md.sh "$(json_write "$SANDBOX/.claude/CLAUDE.md")" "ask on .claude/CLAUDE.md"

echo "== phase state is machine-owned =="
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/current_phase")" "block Write onto current_phase"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/task_class")"    "block Write onto task_class"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/TASK_MODE")"     "block Write onto TASK_MODE"
expect_deny gate-bash.sh "$(json_cmd "echo 9 > .claude/state/current_phase")"   "block shell write onto current_phase"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/task_class")"      "block shell write onto task_class"
expect_deny gate-bash.sh "$(json_cmd "rm .claude/state/TASK_MODE")"             "block deleting TASK_MODE"
# Retargeted from .claude/hooks/advance.sh when the enforcement layer became
# consent-gated. The point of this assertion is that narrowing .claude/hooks/**
# did not lock the whole of .claude/, so it now uses a path that is still meant
# to be freely writable.
expect_allow gate-edit.sh "$(json_write "$SANDBOX/.claude/state/edits.log")"    "other .claude writes still allowed"
expect_allow gate-bash.sh "$(json_cmd "echo 0 > .claude/state/subagent_count")" "counter reset stays available"

echo "== R2: protected paths blocked for Edit/Write =="
expect_deny  gate-edit.sh "$(json_write "C:/repo/Server/.env")"                  "block write: .env"
expect_deny  gate-edit.sh "$(json_write "C:/repo/orchistration/deployment.json")" "block write: deployment.json"
expect_deny  gate-edit.sh "$(json_write "C:/repo/orchistration/cloudflared/cert.pem")" "block write: cloudflared cert"
expect_deny  gate-edit.sh "$(json_write "C:/repo/Server/corner.db")"             "block write: sqlite db"
expect_allow gate-edit.sh "$(json_write "C:/repo/orchistration/cloudflared/.gitkeep")" "allow write: cloudflared .gitkeep"
expect_allow gate-edit.sh "$(json_write "C:/repo/Server/Program.cs")"            "allow write: normal source at phase 5"

echo "== read-only phases block edits =="
for p in 1 2 3 4 8; do
  set_phase "$p"
  expect_deny gate-edit.sh "$(json_write "$SANDBOX/Server/Program.cs")" "block edit at read-only phase $p"
done
for p in 5 6 7 9; do
  set_phase "$p"
  expect_allow gate-edit.sh "$(json_write "$SANDBOX/Server/Program.cs")" "allow edit at phase $p"
done
set_phase 1
expect_allow gate-edit.sh "$(json_write "$SANDBOX/.claude/state/scratch.txt")" "allow .claude write at phase 1"
# Both surfaces must agree on what stays writable in a read-only phase, or a
# scratch file is blocked from Write while the identical `>` is allowed.
SCRATCH="C:/Users/x/AppData/Local/Temp/claude/proj/sess/scratchpad/note.json"
expect_allow gate-edit.sh "$(json_write "$SCRATCH")"                   "allow scratchpad write at phase 1"
expect_allow gate-bash.sh "$(json_cmd "echo x > $SCRATCH")"            "allow scratchpad redirect at phase 1"
task_mode_off
expect_allow gate-edit.sh "$(json_write "$SANDBOX/Server/Program.cs")" "allow edit at phase 1 with task mode off"
task_mode_on
set_phase 5

echo "== read-only phases block shell writes too =="
set_phase 2
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_deny gate-bash.sh "$(json_cmd "$c")" "read-only block: $c"
done <<'CASES'
echo x > Server/notes.txt
sed -i s/a/b/ Server/Program.cs
cp Server/a.cs Server/b.cs
mv Server/a.cs Server/b.cs
touch Server/new.cs
mkdir Server/newdir
dotnet build | tee build.log
git add Server/Program.cs
git commit -m wip
git checkout -b feature
CASES
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_allow gate-bash.sh "$(json_cmd "$c")" "read-only allow: $c"
done <<'CASES'
grep -rn WebAppVersion Server
cat Server/Program.cs
ls -la Server
git log --oneline -5
git diff --stat
git status
find . -name *.cs
dotnet --version
bash .claude/hooks/advance.sh
echo 0 > .claude/state/subagent_count
CASES
set_phase 5

echo "== NotebookEdit payload is understood by the edit guard =="
expect_deny gate-edit.sh '{"tool_name":"NotebookEdit","tool_input":{"notebook_path":"C:/repo/Server/.env"}}' "block NotebookEdit onto .env"

echo "== MCP file-writing tools denied, others allowed =="
expect_deny  gate-mcp-write.sh "$(json_mcp "mcp__figma__create_new_file")"   "block mcp create_new_file"
expect_deny  gate-mcp-write.sh "$(json_mcp "mcp__figma__upload_assets")"     "block mcp upload_assets"
expect_deny  gate-mcp-write.sh "$(json_mcp "mcp__figma__download_assets")"   "block mcp download_assets"
expect_allow gate-mcp-write.sh "$(json_mcp "mcp__browser__tabs_create")"     "allow mcp tabs_create"
expect_allow gate-mcp-write.sh "$(json_mcp "mcp__browser__form_input")"      "allow mcp form_input"
expect_allow gate-mcp-write.sh "$(json_mcp "mcp__browser__read_page")"       "allow mcp read_page"

echo "== advance state machine - phase 1 task-class gate =="
clear_spawns
set_phase 1; clear_class
adv                            && report 0 "phase 1 refuses a bare advance" "(accepted)" || report 1 "phase 1 refuses a bare advance"
phase_is 1                     && report 1 "phase unchanged after refusal"  || report 0 "phase unchanged after refusal"
adv "Trivial"                  && report 0 "phase 1 refuses wrong case"     "(accepted)" || report 1 "phase 1 refuses wrong case"
adv "trivial task"             && report 0 "phase 1 refuses trailing text"  "(accepted)" || report 1 "phase 1 refuses trailing text"
adv "triv"                     && report 0 "phase 1 refuses a truncation"   "(accepted)" || report 1 "phase 1 refuses a truncation"
adv "project level"            && report 0 "phase 1 refuses a retired range string" "(accepted)" || report 1 "phase 1 refuses a retired range string"
phase_is 1                     && report 1 "phase still 1 after four refusals" || report 0 "phase still 1 after refusals"

echo "== the skip table is hardcoded =="
set_phase 1; clear_class
adv "trivial"; phase_is 5 && report 1 "trivial: 1 -> 5 (2,3,4 skipped)" || report 0 "trivial skip" "(phase=$(cat "$STATE/current_phase"))"
set_phase 1; clear_class
adv "standard"; phase_is 2 && report 1 "standard: 1 -> 2 (no skip)" || report 0 "standard skip" "(phase=$(cat "$STATE/current_phase"))"
[[ "$(cat "$STATE/task_class")" == "standard" ]] && report 1 "task class recorded verbatim" || report 0 "task class recorded"

echo "== a class argument is rejected outside phase 1 =="
set_phase 2; set_class "standard"
adv "trivial" && report 0 "class arg rejected at phase 2" "(accepted)" || report 1 "class arg rejected at phase 2"
phase_is 2 && report 1 "phase unchanged after stray class arg" || report 0 "phase unchanged after stray class arg"

echo "== skipped phases are unreachable even if hand-edited =="
for p in 2 3 4; do
  set_phase "$p"; set_class "trivial"
  adv && report 0 "phase $p unreachable for trivial" "(advanced)" || report 1 "phase $p unreachable for trivial"
done
set_phase 2; set_class "standard"
adv; phase_is 3 && report 1 "phase 2 reachable for standard" || report 0 "phase 2 reachable for standard"

echo "== a missing class blocks everything past phase 1 =="
set_phase 5; clear_class
adv && report 0 "no class recorded blocks advance" "(advanced)" || report 1 "no class recorded blocks advance"

echo "== the full walks =="
set_phase 1; clear_class; clear_spawns
adv "standard"                          # 1 -> 2
for _ in 1 2 3 4 5 6 7; do adv; done    # 2 -> 9
phase_is 9 && report 1 "standard walk reaches 9" || report 0 "standard walk" "(phase=$(cat "$STATE/current_phase"))"
adv && report 0 "advance refuses at the terminal phase" "(advanced)" || report 1 "advance refuses at the terminal phase"

set_phase 1; clear_class; clear_spawns
adv "trivial"                           # 1 -> 5
for _ in 1 2 3 4; do adv; done          # 5 -> 9
phase_is 9 && report 1 "trivial walk reaches 9" || report 0 "trivial walk" "(phase=$(cat "$STATE/current_phase"))"

echo "== subagent-busy gate blocks phase change =="
set_phase 5; set_class "standard"; echo 1 > "$STATE/subagent_count"
adv && report 0 "advance blocked while a subagent runs" "(advanced)" || report 1 "advance blocked while a subagent runs"
set_phase 6
rollback && report 0 "rollback blocked while a subagent runs" "(rolled back)" || report 1 "rollback blocked while a subagent runs"
clear_spawns

echo "== rollback 6 -> 1 =="
set_phase 6; set_class "standard"
rollback; phase_is 1 && report 1 "rollback moves 6 -> 1" || report 0 "rollback moves 6 -> 1" "(phase=$(cat "$STATE/current_phase"))"
[[ ! -f "$STATE/task_class" ]] && report 1 "rollback clears the task class" || report 0 "rollback clears the class"
for p in 1 2 3 4 5 7 8 9; do
  set_phase "$p"; set_class "standard"
  rollback && report 0 "rollback refused at phase $p" "(rolled back)" || report 1 "rollback refused at phase $p"
done

echo "== sub-agents are not allowed, at any phase, in any form =="
# CLAUDE.md: "sub-agents are not allowed". The gate takes no phase, model or
# payload shape into account, so there is no input that produces an allow.
set_class "standard"
for p in 1 2 3 4 5 6 7 8 9; do
  set_phase "$p"; clear_spawns
  expect_deny gate-subagent.sh "$(json_agent sonnet)" "phase $p denies a sonnet spawn"
  expect_deny gate-subagent.sh "$(json_agent haiku)"  "phase $p denies a haiku spawn"
done
set_phase 2; clear_spawns
expect_deny gate-subagent.sh "$(json_agent opus)"                                "opus denied"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{"prompt":"p"}}'  "spawn with no model denied"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{"model":"sonnet","options":{"model":"sonnet"}}}' "duplicate model keys denied"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{}}'              "empty tool_input denied"
expect_deny gate-subagent.sh '{}'                                                 "malformed payload denied"
# The library must agree: no argument yields a policy.
( source "$HOOKS/lib/guard-common.sh"
  ok=1
  for n in 1 2 3 4 5 6 7 8 9 10 99 ""; do
    phase_subagent_policy "$n" >/dev/null 2>&1 && { printf 'SP_FAIL [%s]\n' "$n"; ok=0; }
  done
  [[ "$ok" == "1" ]] && printf 'SP_OK\n'
) > "$SANDBOX/sp.out" 2>&1
grep -q SP_OK "$SANDBOX/sp.out" && report 1 "phase_subagent_policy never returns a policy" || report 0 "phase_subagent_policy" "($(tr '\n' ' ' < "$SANDBOX/sp.out"))"

echo "== a refused spawn is never counted =="
clear_spawns
printf '%s' "$(json_agent sonnet)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
c=$(cat "$STATE/subagent_count")
[[ "$c" == "0" ]] && report 1 "refused spawn does not increment the counter" || report 0 "refused spawn incremented" "(count=$c)"
[[ ! -f "$STATE/spawns_this_phase" ]] && report 1 "refused spawn writes no budget record" || report 0 "refused spawn wrote a budget record"

echo "== MCP tools that spawn agents are refused too =="
for t in mcp__x__spawn_task mcp__x__create_agent mcp__x__start_session mcp__x__run_task mcp__x__agent_create mcp__x__delegate_work mcp__x__launch_worker mcp__x__dispatch_job; do
  expect_deny gate-mcp-write.sh "$(json_mcp "$t")" "mcp spawn denied: $t"
done
for t in mcp__browser__read_page mcp__browser__form_input mcp__x__list_sessions mcp__x__get_task_status; do
  expect_allow gate-mcp-write.sh "$(json_mcp "$t")" "mcp non-spawn allowed: $t"
done

echo "== the busy gate survives as defence in depth =="
# Nothing should increment the counter any more, but an agent can still start
# through a surface the Agent matcher never sees (the Skill tool, for one). If
# that happens the phase machine must still refuse to move.
set_phase 5; set_class "standard"
echo 1 > "$STATE/subagent_count"
adv && report 0 "advance blocked while the counter is above zero" "(advanced)" || report 1 "advance blocked while the counter is above zero"
out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q '"decision":"block"' && report 1 "Stop blocked while the counter is above zero" || report 0 "Stop blocked while busy"
printf '{}' | bash "$HOOKS/track-subagent-stop.sh" >/dev/null 2>&1
[[ "$(cat "$STATE/subagent_count")" == "0" ]] && report 1 "SubagentStop still decrements" || report 0 "SubagentStop decrement"
printf '{}' | bash "$HOOKS/track-subagent-stop.sh" >/dev/null 2>&1
[[ "$(cat "$STATE/subagent_count")" == "0" ]] && report 1 "counter floors at zero" || report 0 "counter floors at zero"
clear_spawns

echo "== fail-closed when the guard library is missing =="
mv "$HOOKS/lib/guard-common.sh" "$HOOKS/lib/guard-common.sh.bak"
set_phase 5
expect_deny gate-bash.sh "$(json_cmd "ls")"                          "gate-bash denies without its library"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/Server/Program.cs")" "gate-edit denies without its library"
expect_deny gate-subagent.sh "$(json_agent sonnet)"                   "gate-subagent denies without its library"
out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q '"decision":"block"' && report 1 "gate-stop blocks without its library" || report 0 "gate-stop blocks without its library"
mv "$HOOKS/lib/guard-common.sh.bak" "$HOOKS/lib/guard-common.sh"

echo "== gate-pr-base still enforces --base main =="
expect_deny  gate-pr-base.sh "$(json_cmd "gh pr create --title x")"              "block pr create without --base"
expect_deny  gate-pr-base.sh "$(json_cmd "gh pr create --base develop --title x")" "block pr create --base develop"
expect_allow gate-pr-base.sh "$(json_cmd "gh pr create --base main --title x")"  "allow pr create --base main"

echo "== merging/approving a PR is the user's decision =="
expect_deny  gate-pr-base.sh "$(json_cmd "gh pr merge 154 --squash")"            "block gh pr merge"
expect_deny  gate-pr-base.sh "$(json_cmd "gh pr review 154 --approve")"          "block gh pr review --approve"
expect_deny  gate-pr-base.sh "$(json_cmd "gh api -X PUT repos/o/r/pulls/154/merge")" "block merge via gh api"
expect_allow gate-pr-base.sh "$(json_cmd "gh pr view 154 --json state")"         "allow gh pr view"
expect_allow gate-pr-base.sh "$(json_cmd "gh pr list --state open")"             "allow gh pr list"

echo "== gitignored runtime files are protected like deployment.json =="
expect_deny gate-edit.sh "$(json_write "C:/repo/Server/tokens.json")"            "block write: tokens.json"
expect_deny gate-edit.sh "$(json_write "C:/repo/Server/communicates/note.txt")"  "block write: communicates entry"
expect_deny gate-edit.sh "$(json_write "C:/repo/Server/server_logs/2026-01.txt")" "block write: server log"
expect_deny gate-edit.sh "$(json_write "C:/repo/Server/corner.db-wal")"          "block write: sqlite sidecar"
expect_deny gate-bash.sh "$(json_cmd "rm -f Server/tokens.json")"                "block delete: tokens.json"
expect_deny gate-bash.sh "$(json_cmd "rm -rf Server/server_logs")"               "block delete: server_logs"
expect_allow gate-bash.sh "$(json_cmd "grep -rn error server_logs/")"            "reading server_logs stays allowed"

echo "== phase 8 (reporting) shell whitelist =="
set_phase 8; set_class "standard"; clear_spawns
expect_deny  gate-bash.sh "$(json_cmd "dotnet build")"                          "block bash at phase 8"
expect_deny  gate-bash.sh "$(json_cmd "cat Server/Program.cs")"                 "block even reads at phase 8"
expect_allow gate-bash.sh "$(json_cmd "bash .claude/hooks/advance.sh")"         "allow advance.sh at phase 8"
expect_allow gate-bash.sh "$(json_cmd "gh pr create --base main --title x")"    "allow gh pr create at phase 8"
set_phase 5

echo "== prompt classification =="
prompt_json() { printf '{"prompt":"%s"}' "$1"; }
set_phase 9
out=$(printf '%s' "$(prompt_json "why did you fix that?")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
p=$(cat "$STATE/current_phase")
[[ "$p" == "9" && -z "$out" ]] && report 1 "question with '?' does not reset the phase" || report 0 "question does not reset" "(phase=$p out=$out)"

echo "== redirect detection is quote-aware =="
# Two earlier designs both failed here: stripping quoted spans hid
# `echo x > "CLAUDE.md"`, and removing quote characters turned DATA containing
# '>' into a redirect (a printf of XML was read as a write and blocked in a
# read-only phase). Both directions are pinned.
( source "$HOOKS/lib/guard-common.sh"
  rcheck() {
    local desc="$1" input="$2" expected="$3" got
    got=$(redirect_targets "$input" | tr '\n' ' '); got="${got% }"
    [[ "$got" == "$expected" ]] && printf 'RC_OK\n' || printf 'RC_FAIL %s got[%s] want[%s]\n' "$desc" "$got" "$expected"
  }
  rcheck "xml in single quotes"   "printf '<task-id>x</task-id>' | sh"     ""
  rcheck "xml in double quotes"   'printf "<a>b</a>"'                      ""
  rcheck "gt inside grep pattern" "grep -n 'a > b' f.txt"                  ""
  rcheck "gt inside commit msg"   'git commit -m "note: a > b here"'       ""
  rcheck "plain redirect"         'echo x > CLAUDE.md'                     "CLAUDE.md"
  rcheck "quoted target"          'echo x > "CLAUDE.md"'                   "CLAUDE.md"
  rcheck "append single-quoted"   "echo x >> 'Watchdog/CLAUDE.md'"         "Watchdog/CLAUDE.md"
  rcheck "stderr dup"             'dotnet build 2>&1'                      ""
  rcheck "dev null filtered"      'cat a > /dev/null'                      ""
  rcheck "phase state redirect"   'echo 9 > .claude/state/current_phase'   ".claude/state/current_phase"
  rcheck "task class redirect"    'echo x > .claude/state/task_class'      ".claude/state/task_class"
  rcheck "two redirects"          'a > one.txt; b >> two.txt'              "one.txt two.txt"
  rcheck "escaped gt"             'echo a \> b'                            ""
) > "$SANDBOX/rcheck.out" 2>&1
while IFS= read -r line; do
  case "$line" in
    RC_OK) report 1 "redirect scanner case" ;;
    RC_FAIL*) report 0 "redirect scanner: ${line#RC_FAIL }" ;;
  esac
done < "$SANDBOX/rcheck.out"
# End to end: a read-only phase must not block a read whose DATA contains '>'.
set_phase 2; set_class "standard"
expect_allow gate-bash.sh "$(json_cmd "printf '<task-id>x</task-id>'")"  "read-only allows data containing '>'"
expect_allow gate-bash.sh "$(json_cmd "grep -n 'a > b' Server/Program.cs")" "read-only allows grep for a '>' pattern"
set_phase 5

echo "== automation events must never move the phase =="
# Observed live: a background-task completion notification reached
# UserPromptSubmit at the terminal phase, carried no '?', and the exit rule reset
# the phase to 1 mid-session. Harness turns are not requests.
while IFS= read -r q; do
  [[ -z "$q" ]] && continue
  set_phase 9; set_class "standard"
  out=$(printf '%s' "$(prompt_json "$q")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
  p=$(cat "$STATE/current_phase")
  [[ "$p" == "9" && -z "$out" ]] && report 1 "automation ignored: ${q:0:44}" || report 0 "automation ignored: ${q:0:44}" "(phase=$p)"
  [[ -f "$STATE/task_class" ]] && report 1 "class preserved: ${q:0:44}" || report 0 "class preserved: ${q:0:44}"
done <<'CASES'
[SYSTEM NOTIFICATION - NOT USER INPUT] Background command completed
<task-notification><task-id>b03s4nyee</task-id><status>completed</status></task-notification>
Monitor event fired <task-id>abc</task-id>
<ci-monitor-event>checks finished</ci-monitor-event>
<system-reminder>the file exists but is empty</system-reminder>
CASES
# The same guard must hold at a mid-task phase, and for the keyword path too.
set_phase 5; set_class "standard"
printf '%s' "$(prompt_json "[SYSTEM NOTIFICATION - NOT USER INPUT] update finished")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "automation with a trigger keyword leaves phase 5 alone" || report 0 "automation at phase 5" "(phase=$p)"
# A real request that merely mentions the words must NOT be misread as automation.
set_phase 9; set_class "standard"
printf '%s' "$(prompt_json "the background command failed, please look at it")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "prose about background commands still counts as a task" || report 0 "prose not misread as automation" "(phase=$p)"

echo "== phase 9 exit rule: '?' stays, anything else resets to 1 =="
while IFS= read -r q; do
  [[ -z "$q" ]] && continue
  set_phase 9; set_class "standard"
  out=$(printf '%s' "$(prompt_json "$q")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
  p=$(cat "$STATE/current_phase")
  [[ "$p" == "9" && -z "$out" ]] && report 1 "stays at 9: $q" || report 0 "stays at 9: $q" "(phase=$p)"
  [[ -f "$STATE/task_class" ]] && report 1 "class preserved for: $q" || report 0 "class preserved for: $q"
done <<'CASES'
why did you fix that?
can you explain the phase gate?
add a retry to the uploader? or is that risky
?
CASES

# Everything without a '?' is a new task - including prompts that match no
# implementation keyword, which is the case a keyword-only test lets through.
while IFS= read -r q; do
  [[ -z "$q" ]] && continue
  set_phase 9; set_class "standard"; printf '1:1' > "$STATE/spawns_this_phase"
  out=$(printf '%s' "$(prompt_json "$q")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
  p=$(cat "$STATE/current_phase")
  [[ "$p" == "1" ]] && report 1 "resets 9 -> 1: $q" || report 0 "resets 9 -> 1: $q" "(phase=$p)"
  [[ ! -f "$STATE/task_class" ]] && report 1 "class cleared for: $q" || report 0 "class cleared for: $q"
  [[ ! -f "$STATE/spawns_this_phase" ]] && report 1 "spawn budget cleared for: $q" || report 0 "spawn budget cleared for: $q"
  printf '%s' "$out" | grep -q "Phase reset" && report 1 "reset announced for: $q" || report 0 "reset announced for: $q"
done <<'CASES'
make the login page faster
one more pass on the uploader
thanks, looks good
now the cockpit tab
CASES

# Below the terminal phase the reset rule does not apply.
set_phase 5; set_class "standard"
printf '%s' "$(prompt_json "thanks, looks good")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "phase 5 is untouched by the terminal-phase rule" || report 0 "phase 5 untouched" "(phase=$p)"

echo "== SessionStart preserves state on compact/resume and while busy =="
set_phase 5; echo 0 > "$STATE/subagent_count"
printf '{"session_id":"s1","source":"compact"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "compact does not reset the phase" || report 0 "compact does not reset" "(phase=$p)"

set_phase 5
printf '{"session_id":"s1","source":"resume"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "resume does not reset the phase" || report 0 "resume does not reset" "(phase=$p)"

set_phase 5; echo 2 > "$STATE/subagent_count"
printf '{"session_id":"s1","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "startup while subagents active does not reset" || report 0 "busy startup does not reset" "(phase=$p)"

# The case actually observed: a SessionStart arriving mid-task reporting
# source=startup, busy=0, new id.
set_phase 7; echo 0 > "$STATE/subagent_count"; rm -f "$STATE/last_session_id"
printf '{"session_id":"s7","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "7" ]] && report 1 "mid-task startup does not reset a working phase" || report 0 "mid-task startup preserved" "(phase=$p)"

set_phase 8; echo 0 > "$STATE/subagent_count"; rm -f "$STATE/last_session_id"
printf '{"session_id":"s8","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "8" ]] && report 1 "phase 8 counts as a working phase" || report 0 "phase 8 preserved" "(phase=$p)"

set_phase 5; echo 0 > "$STATE/subagent_count"
printf '%s' "s1" > "$STATE/last_session_id"
printf '{"session_id":"s1","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "same session id re-fire does not reset" || report 0 "same id preserved" "(phase=$p)"

# Resting phases stay resettable, including after the 30-minute window.
set_phase 9; set_class "standard"; echo 0 > "$STATE/subagent_count"; rm -f "$STATE/last_session_id"
printf '{"session_id":"s2","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "genuine startup at a resting phase resets to 1" || report 0 "startup resets" "(phase=$p)"
[[ ! -f "$STATE/task_class" ]] && report 1 "session reset clears the task class" || report 0 "session reset clears the class"

set_phase 5; touch -d '2 hours ago' "$STATE/current_phase" 2>/dev/null
rm -f "$STATE/last_session_id"
printf '{"session_id":"s3","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "stale working phase (>30 min) is reset" || report 0 "stale phase reset" "(phase=$p)"

[[ "$(cat "$STATE/last_session_id" 2>/dev/null)" == "s3" ]] && report 1 "session id recorded for re-fire detection" || report 0 "session id recorded"

echo "== phase model migration: a number from a retired model is never trusted =="
# A stale stamp must override EVERY preserve signal. Phase 5 with a fresh mtime
# and source=compact is the strongest preserve case there is.
set_phase 5; set_class "standard"; echo 0 > "$STATE/subagent_count"
set_model 1
printf '%s' "s1" > "$STATE/last_session_id"
out=$(printf '{"session_id":"s1","source":"compact"}' | bash "$HOOKS/session-start.sh" 2>/dev/null)
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "stale model forces a reset even on compact" || report 0 "stale model reset" "(phase=$p)"
[[ ! -f "$STATE/task_class" ]] && report 1 "stale model clears the task class" || report 0 "stale model clears the class"
[[ "$(cat "$STATE/phase_model")" == "$MODEL_VERSION" ]] && report 1 "session-start restamps the model version" || report 0 "session-start restamps"
printf '%s' "$out" | grep -q "retired model\|different model" && report 1 "migration is announced, not silent" || report 0 "migration announced"

# advance.sh and rollback.sh normalise rather than act on an uninterpretable number.
set_phase 5; set_class "standard"; set_model 1; clear_spawns
adv && report 0 "advance refuses under a stale model" "(advanced)" || report 1 "advance refuses under a stale model"
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "advance normalises the phase to 1" || report 0 "advance normalises" "(phase=$p)"
[[ "$(cat "$STATE/phase_model")" == "$MODEL_VERSION" ]] && report 1 "advance restamps the model version" || report 0 "advance restamps"

set_phase 6; set_class "standard"; set_model 1; clear_spawns
rollback && report 0 "rollback refuses under a stale model" "(rolled back)" || report 1 "rollback refuses under a stale model"
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "rollback normalises the phase to 1" || report 0 "rollback normalises" "(phase=$p)"

# An unstamped state directory is treated the same way as a stale one.
set_phase 5; set_class "standard"; rm -f "$STATE/phase_model"; clear_spawns
adv && report 0 "advance refuses with no model stamp" "(advanced)" || report 1 "advance refuses with no model stamp"
[[ "$(cat "$STATE/current_phase")" == "1" ]] && report 1 "unstamped state normalises to phase 1" || report 0 "unstamped normalises"
set_phase 5; set_class "standard"

echo "== Stop gate =="
task_mode_on; set_class "standard"; echo 0 > "$STATE/subagent_count"
: > "$STATE/rebuild_pending.log"; : > "$STATE/hook_errors.log"
for p in 1 2 3 5 7 8; do
  set_phase "$p"
  out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
  printf '%s' "$out" | grep -q '"decision":"block"' && report 1 "Stop blocked at phase $p" || report 0 "Stop blocked at phase $p"
done
for p in 4 6 9; do
  set_phase "$p"
  out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
  printf '%s' "$out" | grep -q '"decision":"block"' && report 0 "Stop allowed at phase $p" "(blocked)" || report 1 "Stop allowed at phase $p"
done

set_phase 9; echo 1 > "$STATE/subagent_count"
out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q '"decision":"block"' && report 1 "Stop blocked while a subagent is active" || report 0 "Stop blocked while busy"
echo 0 > "$STATE/subagent_count"

set_phase 9; printf 'x\n' > "$STATE/rebuild_pending.log"
out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q '"decision":"block"' && report 1 "Stop blocked while a CalcEngine rebuild is pending" || report 0 "Stop blocked on pending rebuild"
: > "$STATE/rebuild_pending.log"

set_phase 9; printf 'boom\n' > "$STATE/hook_errors.log"
out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q 'additionalContext' && report 1 "hook errors surfaced without blocking" || report 0 "hook errors surfaced"
: > "$STATE/hook_errors.log"

out=$(printf '{"stop_hook_active":true}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
[[ -z "$out" ]] && report 1 "stop_hook_active short-circuits" || report 0 "stop_hook_active short-circuits"

task_mode_off
set_phase 1
out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q '"decision":"block"' && report 0 "task mode off disables the Stop gate" "(blocked)" || report 1 "task mode off disables the Stop gate"
task_mode_on

echo "== a guard that ABORTS must fail closed, not silently allow =="
# Found by probing: every gate read ${CLAUDE_PROJECT_DIR} under `set -u`, so an
# unset variable killed the script before it printed a decision. A PreToolUse
# hook that exits non-zero with no JSON is treated as "no opinion" and the tool
# call proceeds. That is how a shell write to current_phase got through during a
# folder swap, when the hook file was momentarily absent.
for h in gate-bash.sh gate-edit.sh gate-subagent.sh; do
  out=$(printf '%s' "$(json_cmd "ls")" | env -u CLAUDE_PROJECT_DIR bash "$HOOKS/$h" 2>/dev/null)
  printf '%s' "$out" | grep -q '"permissionDecision":"deny"' \
    && report 1 "$h denies when CLAUDE_PROJECT_DIR is unset" \
    || report 0 "$h fails open when CLAUDE_PROJECT_DIR is unset"
done
out=$(printf '{"stop_hook_active":false}' | env -u CLAUDE_PROJECT_DIR bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q '"decision":"block"' \
  && report 1 "gate-stop blocks when CLAUDE_PROJECT_DIR is unset" \
  || report 0 "gate-stop fails open when CLAUDE_PROJECT_DIR is unset"

echo "== the phase-command whitelist is per-segment, all segments must pass =="
# It used to substring-match the whole raw command, so appending the path in a
# comment, or joining it with &&, skipped the read-only and phase-8 checks for
# the entire line.
set_phase 8; set_class "standard"; clear_spawns
expect_allow gate-bash.sh "$(json_cmd "bash .claude/hooks/advance.sh")"                        "phase 8 allows a bare advance.sh"
expect_allow gate-bash.sh "$(json_cmd "cd C:/x && bash .claude/hooks/advance.sh")"             "phase 8 allows cd then advance.sh"
expect_allow gate-bash.sh "$(json_cmd "bash .claude/hooks/rollback.sh")"                       "phase 8 allows rollback.sh"
expect_deny  gate-bash.sh "$(json_cmd "dotnet build && bash .claude/hooks/advance.sh")"        "phase 8 denies work smuggled beside advance.sh"
expect_deny  gate-bash.sh "$(json_cmd "dotnet build # .claude/hooks/advance.sh")"              "phase 8 denies advance.sh in a comment"
expect_deny  gate-bash.sh "$(json_cmd "dotnet build; bash .claude/hooks/advance.sh")"          "phase 8 denies a semicolon chain"
set_phase 2
expect_deny  gate-bash.sh "$(json_cmd "echo x > Server/pwn.cs && bash .claude/hooks/advance.sh")" "read-only denies a write smuggled beside advance.sh"
expect_deny  gate-bash.sh "$(json_cmd "echo .claude/hooks/advance.sh > Server/pwn.cs")"        "read-only denies advance.sh as echoed data"
set_phase 1
expect_allow gate-bash.sh "$(json_cmd "bash .claude/hooks/advance.sh \\\"standard\\\"")"       "phase 1 allows advance.sh with its class argument"

echo "== path traversal is resolved before matching =="
set_phase 5; set_class "standard"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/../state/TASK_MODE")"   "traversal onto TASK_MODE blocked"
expect_deny gate-bash.sh "$(json_cmd "echo x > Server/../CLAUDE.md")"                "traversal onto the root CLAUDE.md blocked"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/../state/current_phase")" "Write traversal onto current_phase blocked"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/Server/../CLAUDE.md")"              "Write traversal onto the root CLAUDE.md blocked"
expect_deny gate-claude-md.sh "$(json_write "$SANDBOX/Server/../CLAUDE.md")"         "second layer also resolves traversal"
expect_allow gate-edit.sh "$(json_write "$SANDBOX/Server/sub/../Program.cs")"        "harmless traversal inside the repo still allowed"
( source "$HOOKS/lib/guard-common.sh"
  [[ "$(normalize_path 'Server/../CLAUDE.md')" == "CLAUDE.md" ]] || printf 'NP_FAIL a\n'
  [[ "$(normalize_path '.claude/state/../state/TASK_MODE')" == ".claude/state/TASK_MODE" ]] || printf 'NP_FAIL b\n'
  [[ "$(normalize_path 'C:/a/b/../c')" == "C:/a/c" ]] || printf 'NP_FAIL c\n'
  [[ "$(normalize_path './x/./y')" == "x/y" ]] || printf 'NP_FAIL d\n'
  [[ "$(normalize_path 'a\\b')" == "a/b" ]] || printf 'NP_FAIL e\n'
  printf 'NP_DONE\n'
) > "$SANDBOX/np.out" 2>&1
grep -q NP_FAIL "$SANDBOX/np.out" && report 0 "normalize_path unit cases" "($(tr '\n' ' ' < "$SANDBOX/np.out"))" || report 1 "normalize_path unit cases"

echo "== guarded paths match case-insensitively (NTFS) =="
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/task_mode")"        "lowercase task_mode blocked"
expect_deny gate-bash.sh "$(json_cmd "echo x > .CLAUDE/state/current_phase")"    "uppercase .CLAUDE blocked"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/TASK_mode")"      "mixed-case TASK_mode blocked for Write"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/Server/.ENV")"                  "uppercase .ENV still protected"

echo "== the scratchpad exemption is the scratchpad, not all of Temp =="
set_phase 2
expect_deny  gate-bash.sh "$(json_cmd "echo x > C:/Users/x/AppData/Local/Temp/unrelated.txt")" "unrelated temp file is not exempt"
expect_allow gate-bash.sh "$(json_cmd "echo x > C:/Users/x/AppData/Local/Temp/claude/p/s/scratchpad/n.txt")" "session scratchpad is exempt"
expect_deny  gate-edit.sh "$(json_write "C:/Users/x/AppData/Local/Temp/unrelated.txt")"        "unrelated temp file is not exempt for Write"

echo "== the enforcement layer cannot be changed silently =="
set_phase 5
# These three asserted permissionDecision:"ask". Probing showed an ask does not
# gate anything in this client, so the protection was replaced by a deny keyed
# on a token the agent cannot mint. The consent block at the end of this file
# covers the replacement contract in both directions.
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/settings.json")"             "settings.json write denied without consent"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/hooks/gate-bash.sh")"        "hook script write denied without consent"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/hooks/lib/guard-common.sh")" "guard library write denied without consent"
expect_deny  gate-bash.sh "$(json_cmd "echo {} > .claude/settings.json")"             "shell write onto settings.json denied"
expect_deny  gate-bash.sh "$(json_cmd "echo x > .claude/hooks/gate-bash.sh")"         "shell write onto a hook script denied"
expect_deny  gate-bash.sh "$(json_cmd "rm .claude/hooks/gate-stop.sh")"               "shell delete of a hook script denied"
expect_allow gate-edit.sh "$(json_write "$SANDBOX/.claude/state/subagent_count")"     "the documented recovery valve stays writable"

echo "== an inline interpreter payload is scanned for redirects =="
set_phase 2
expect_deny  gate-bash.sh "$(json_cmd "bash -c \\\"echo x > Server/pwn.cs\\\"")"      "bash -c redirect blocked in a read-only phase"
expect_deny  gate-bash.sh "$(json_cmd "sh -c \\\"echo x > Server/pwn.cs\\\"")"        "sh -c redirect blocked in a read-only phase"
expect_deny  gate-bash.sh "$(json_cmd "python -c \\\"open('Server/pwn.cs','w')\\\" > out.txt")" "interpreter with an outer redirect blocked"
# The quote-aware reader must still not fire on data that merely contains '>'.
expect_allow gate-bash.sh "$(json_cmd "grep -n 'a > b' Server/Program.cs")"           "grep for a '>' pattern still allowed"
expect_allow gate-bash.sh "$(json_cmd "printf '<a>b</a>'")"                           "printf of XML data still allowed"
set_phase 5; set_class "standard"

echo "== hook-edit consent: the passphrase is the only way in =="
PASSPHRASE="yes claude allowed to modify claude settings"
grant_off() { rm -f "$STATE/hook_edit_grant"; }
grant_on()  { : > "$STATE/hook_edit_grant"; }

set_phase 5; set_class "standard"; grant_off

# Denied without consent, from both surfaces.
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/settings.json")"             "no grant: settings.json Write denied"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/hooks/gate-bash.sh")"        "no grant: hook script Write denied"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/hooks/lib/guard-common.sh")" "no grant: guard library Write denied"
expect_deny gate-bash.sh "$(json_cmd "echo {} > .claude/settings.json")"              "no grant: settings.json shell write denied"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/hooks/gate-stop.sh")"          "no grant: hook script shell write denied"

# Permitted with consent.
grant_on
expect_allow gate-edit.sh "$(json_write "$SANDBOX/.claude/settings.json")"             "grant: settings.json Write allowed"
expect_allow gate-edit.sh "$(json_write "$SANDBOX/.claude/hooks/gate-bash.sh")"        "grant: hook script Write allowed"
expect_allow gate-bash.sh "$(json_cmd "echo x > .claude/hooks/gate-stop.sh")"          "grant: hook script shell write allowed"
# The grant covers the enforcement layer only; the phase state stays absolute.
expect_deny  gate-edit.sh "$(json_write "$SANDBOX/.claude/state/current_phase")"       "grant does NOT unlock current_phase"
expect_deny  gate-bash.sh "$(json_cmd "echo 9 > .claude/state/task_class")"            "grant does NOT unlock task_class"
expect_deny  gate-edit.sh "$(json_write "$SANDBOX/CLAUDE.md")"                         "grant does NOT unlock the root CLAUDE.md"
expect_deny  gate-edit.sh "$(json_write "$SANDBOX/Server/.env")"                       "grant does NOT unlock protected assets"
grant_off

echo "== the agent cannot mint the grant itself =="
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/hook_edit_grant")"      "Write onto hook_edit_grant denied"
expect_deny gate-bash.sh "$(json_cmd "touch .claude/state/hook_edit_grant")"           "touch hook_edit_grant denied"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/hook_edit_grant")"        "redirect onto hook_edit_grant denied"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/../state/hook_edit_grant")" "traversal onto hook_edit_grant denied"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/HOOK_EDIT_GRANT")"        "wrong-case hook_edit_grant denied"

echo "== only the exact passphrase creates the grant =="
set_phase 9; set_class "standard"; grant_off
# Exact match: grant created, and the phase must NOT be reset even though the
# passphrase carries no '?' - it is consent, not a new task.
out=$(printf '%s' "$(prompt_json "$PASSPHRASE")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
[[ -f "$STATE/hook_edit_grant" ]] && report 1 "exact passphrase creates the grant" || report 0 "exact passphrase creates the grant"
[[ "$(cat "$STATE/current_phase")" == "9" ]] && report 1 "passphrase does not reset the phase" || report 0 "passphrase reset the phase" "(phase=$(cat "$STATE/current_phase"))"
printf '%s' "$out" | grep -q "HOOK EDIT GRANT ACTIVE" && report 1 "grant is announced to the agent" || report 0 "grant announced"

# Leading and trailing whitespace is tolerated; nothing else is.
grant_off
printf '%s' "$(prompt_json "   $PASSPHRASE  ")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
[[ -f "$STATE/hook_edit_grant" ]] && report 1 "surrounding whitespace tolerated" || report 0 "surrounding whitespace tolerated"

while IFS= read -r q; do
  [[ -z "$q" ]] && continue
  set_phase 9; grant_off
  printf '%s' "$(prompt_json "$q")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
  [[ -f "$STATE/hook_edit_grant" ]] && report 0 "must NOT grant: $q" "(granted)" || report 1 "must NOT grant: $q"
done <<'CASES'
Yes claude allowed to modify claude settings
yes Claude allowed to modify claude settings
yes claude allowed to modify claude setting
yes claude is allowed to modify claude settings
yes  claude allowed to modify claude settings
CASES

# By explicit user decision the phrase grants wherever it appears, so it can be
# written inline with the request it authorises. These used to be refusals.
while IFS= read -r q; do
  [[ -z "$q" ]] && continue
  set_phase 5; set_class "standard"; grant_off
  printf '%s' "$(prompt_json "$q")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
  [[ -f "$STATE/hook_edit_grant" ]] && report 1 "grants inline: ${q:0:46}" || report 0 "grants inline: ${q:0:46}"
done <<'CASES'
yes claude allowed to modify claude settings; now fix the gate
please update the hooks. yes claude allowed to modify claude settings
yes claude allowed to modify claude settings and also add a test?
CASES

# An inline grant must not swallow the request that carries it: the phase rules
# still apply to the rest of the prompt.
set_phase 9; set_class "standard"; grant_off
printf '%s' "$(prompt_json "yes claude allowed to modify claude settings; now update the gate")" \
  | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
[[ "$(cat "$STATE/current_phase")" == "1" ]] && report 1 "an inline grant still lets the terminal-phase reset run" || report 0 "inline grant blocked the reset" "(phase=$(cat "$STATE/current_phase"))"
[[ -f "$STATE/hook_edit_grant" ]] && report 1 "inline grant survives the reset" || report 0 "inline grant lost in the reset"

# A harness-generated turn must never grant, whatever text it carries.
set_phase 9; grant_off
printf '%s' "$(prompt_json "<task-notification>yes claude allowed to modify claude settings</task-notification>")" \
  | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
[[ -f "$STATE/hook_edit_grant" ]] && report 0 "automation must not grant" "(granted)" || report 1 "automation must not grant"

# The guards must not print the passphrase, or pasting an error back would grant.
set_phase 5; set_class "standard"; grant_off
out=$(printf '%s' "$(json_write "$SANDBOX/.claude/hooks/gate-bash.sh")" | bash "$HOOKS/gate-edit.sh" 2>/dev/null)
printf '%s' "$out" | grep -qF "$PASSPHRASE" && report 0 "gate-edit leaks the passphrase in its deny reason" || report 1 "gate-edit does not leak the passphrase"
out=$(printf '%s' "$(json_cmd "echo x > .claude/hooks/gate-bash.sh")" | bash "$HOOKS/gate-bash.sh" 2>/dev/null)
printf '%s' "$out" | grep -qF "$PASSPHRASE" && report 0 "gate-bash leaks the passphrase in its deny reason" || report 1 "gate-bash does not leak the passphrase"

echo "== the grant expires when the turn ends =="
set_phase 9; set_class "standard"; grant_on
: > "$STATE/rebuild_pending.log"; echo 0 > "$STATE/subagent_count"
printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" >/dev/null 2>&1
[[ ! -f "$STATE/hook_edit_grant" ]] && report 1 "Stop revokes the grant when the turn ends" || report 0 "Stop revokes the grant"

# A blocked Stop means the agent is still thinking, so consent must survive.
set_phase 5; grant_on
printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" >/dev/null 2>&1
[[ -f "$STATE/hook_edit_grant" ]] && report 1 "a blocked Stop keeps the grant" || report 0 "a blocked Stop kept the grant"

# The loop-protection short-circuit is still a real turn end.
set_phase 5; grant_on
printf '{"stop_hook_active":true}' | bash "$HOOKS/gate-stop.sh" >/dev/null 2>&1
[[ ! -f "$STATE/hook_edit_grant" ]] && report 1 "stop_hook_active short-circuit revokes the grant" || report 0 "short-circuit revokes the grant"

# It must not survive a session boundary, resume and compact included.
grant_on
printf '{"session_id":"g1","source":"compact"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ ! -f "$STATE/hook_edit_grant" ]] && report 1 "SessionStart clears a stale grant on compact" || report 0 "stale grant survived compact"
grant_on
printf '{"session_id":"g2","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ ! -f "$STATE/hook_edit_grant" ]] && report 1 "SessionStart clears a stale grant on startup" || report 0 "stale grant survived startup"
set_phase 5; set_class "standard"; grant_off

echo
printf 'passed: %d   failed: %d\n' "$PASS" "$FAIL"
[[ "$FAIL" == "0" ]]
