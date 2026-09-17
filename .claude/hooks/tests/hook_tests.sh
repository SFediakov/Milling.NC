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

json_cmd()   { printf '{"tool_name":"Bash","tool_input":{"command":"%s"}}' "$1"; }
json_ps()    { printf '{"tool_name":"PowerShell","tool_input":{"command":"%s"}}' "$1"; }
json_write() { printf '{"tool_name":"Write","tool_input":{"file_path":"%s","content":"x"}}' "$1"; }
# Every real Agent payload carries a tool_use_id (verified live); the ledger
# keys its slot reservation on it, so the helper mints a fresh one per call.
# Nanoseconds plus a zero-padded random suffix: unique, and numerically ordered
# in time, which is what last_res_id (below) relies on. A counter would not do:
# the helper runs inside $(...), where an increment is lost with the subshell.
json_agent() { printf '{"tool_name":"Agent","tool_use_id":"toolu_%s%05d","tool_input":{"description":"%s","model":"%s","prompt":"p"}}' "$(date +%s%N)" "$RANDOM" "${2:-probe}" "$1"; }
ledger_reset() { rm -rf "$STATE/agents"; }
slots_held()   { local f c=0; for f in "$STATE/agents"/slot.*; do [[ -f "$f" ]] && c=$((c + 1)); done; printf '%s' "$c"; }
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
task_mode_on; set_phase 5; set_class "standard"

echo "== phase model is the single source of truth =="
[[ "$LIB_PHASE_DONE" == "9" ]] && report 1 "library PHASE_DONE is 9" || report 0 "library PHASE_DONE" "(got $LIB_PHASE_DONE)"
[[ "$MODEL_VERSION" == "3" ]] && report 1 "library PHASE_MODEL_VERSION is 3" || report 0 "library PHASE_MODEL_VERSION" "(got $MODEL_VERSION)"
( source "$HOOKS/lib/guard-common.sh"
  names="1:Task definition 2:Files pre-research 3:WEB research 4:Planning 5:Execution 6:Testing 7:Documentation 8:Reporting"
  ok=1
  for pair in $names; do :; done
  for n in 1 2 3 4 5 6 7 8; do
    case "$n" in
      1) want="Task definition" ;;
      2) want="Research block: files pre-research + WEB research (concurrent)" ;;
      3) want="retired number - WEB research is half of phase 2" ;;
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

echo "== remote main is PR-only: every push route to main is denied =="
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_deny gate-bash.sh "$(json_cmd "$c")" "block push: $c"
done <<'CASES'
git push origin main
git push origin Main
git push origin +main
git push origin refs/heads/main
git push origin feature:main
git push origin HEAD:main
git push origin HEAD:refs/heads/main
git push origin claude/x main
git push
git push origin
git push -u origin HEAD
git push origin HEAD
git push origin @
git push origin --tags
git -c push.default=current push
git push --all origin
git push --mirror origin
git push --branches origin
git push origin refs/heads/*:refs/heads/*
git push origin 'refs/heads/*'
git -C /tmp/x push origin main
sudo git push origin main
bash -c 'git push origin main'
git checkout main && git push origin main
git push origin claude/x; git push origin main
CASES

echo "== remote main is PR-only: named feature pushes stay allowed =="
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_allow gate-bash.sh "$(json_cmd "$c")" "allow push: $c"
done <<'CASES'
git push -u origin claude/x
git push origin claude/x:refs/heads/claude/x
git push origin claude/x:claude/x
git push origin main:claude/backup
git push origin v1.2.3
git push --dry-run origin claude/x
git -C /tmp/x push origin claude/y
git push origin claude/x 2>&1
git commit -m 'git push origin main'
grep -rn 'git push origin main' .claude/hooks/tests
CASES

echo "== local main only fast-forwards from origin =="
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_deny gate-bash.sh "$(json_cmd "$c")" "block local main rewrite: $c"
done <<'CASES'
git fetch origin feature:main
git fetch origin feature:refs/heads/main
git fetch origin +main:main
git pull origin feature:main
git branch -f main x
git branch --force main x
git branch -M x main
git branch -m x main
git branch -C x main
git branch -d main
git update-ref refs/heads/main abc
git update-ref -d refs/heads/main
git checkout -B main
git checkout -B main origin/main
git switch -C main
git switch --force-create main
CASES
while IFS= read -r c; do
  [[ -z "$c" ]] && continue
  expect_allow gate-bash.sh "$(json_cmd "$c")" "allow main sync/read: $c"
done <<'CASES'
git fetch origin
git fetch origin main
git fetch origin main:main
git pull --ff-only origin main
git pull origin main
git pull
git checkout main
git switch main
git checkout -b main
git switch -c main
git checkout -B claude/x
git switch -C claude/x
git branch -f claude/x origin/claude/x
git branch -u origin/main main
git branch --list main
git merge origin/claude/other
git log --oneline -5 main
git diff main..HEAD
git rev-parse main origin/main
CASES

echo "== the main rules are always-on: any phase, task mode off, PowerShell =="
task_mode_off
expect_deny gate-bash.sh "$(json_cmd "git push origin main")"        "block push main with task mode off"
expect_deny gate-bash.sh "$(json_cmd "git push")"                    "block bare push with task mode off"
expect_deny gate-bash.sh "$(json_ps  "git push origin main")"        "block push main from PowerShell, task mode off"
expect_deny gate-bash.sh "$(json_cmd "git branch -f main x")"        "block local main rewrite with task mode off"
task_mode_on
for p in 1 4 8 9; do
  set_phase "$p"
  expect_deny gate-bash.sh "$(json_cmd "git push origin main")"      "block push main at phase $p"
  expect_deny gate-bash.sh "$(json_ps  "git push origin feature:main")" "block PowerShell push to main at phase $p"
done
set_phase 5

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
expect_allow gate-bash.sh "$(json_cmd "echo x > .claude/state/edits.log")"      "other .claude shell writes still allowed"

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
adv "files"; adv "web"; phase_is 4 && report 1 "phase 2 reachable for standard (joins into 4)" || report 0 "phase 2 reachable for standard" "(phase=$(cat "$STATE/current_phase"))"
# The number 3 is retired: reachable for no class, and never produced.
set_phase 3; set_class "standard"
adv && report 0 "phase 3 unreachable for standard (retired number)" "(advanced)" || report 1 "phase 3 unreachable for standard (retired number)"
phase_is 3 && report 1 "retired 3 is left alone for the user to fix" || report 0 "retired 3 was moved" "(phase=$(cat "$STATE/current_phase"))"

echo "== the research block: fork at 2, join into 4 =="
rflags() { printf 'files=%s web=%s' "$([[ -s "$STATE/research_files.done" ]] && printf yes || printf no)" "$([[ -s "$STATE/research_web.done" ]] && printf yes || printf no)"; }
set_phase 1; clear_class; rm -f "$STATE"/research_*.done
adv "standard"; phase_is 2 && report 1 "standard: 1 -> 2 enters the block" || report 0 "standard enters the block" "(phase=$(cat "$STATE/current_phase"))"
adv           && report 0 "bare advance refused at phase 2" "(advanced)" || report 1 "bare advance refused at phase 2"
adv "trivial" && report 0 "class string refused at phase 2" "(accepted)"  || report 1 "class string refused at phase 2"
adv "Files"   && report 0 "wrong-case half refused" "(accepted)"          || report 1 "wrong-case half refused"
adv "files web" && report 0 "two halves in one call refused" "(accepted)" || report 1 "two halves in one call refused"
phase_is 2 && [[ "$(rflags)" == "files=no web=no" ]] && report 1 "refusals leave the block untouched" || report 0 "refusals touched the block" "($(rflags))"
adv "files" && report 1 "first half accepted" || report 0 "first half accepted"
phase_is 2 && [[ "$(rflags)" == "files=yes web=no" ]] && report 1 "one mark keeps the phase at 2" || report 0 "one mark moved the phase" "(phase=$(cat "$STATE/current_phase") $(rflags))"
grep -q "finished" "$STATE/research_files.done" && report 1 "mark file carries content, not just existence" || report 0 "mark file is empty"
adv "files" && report 0 "repeated half refused" "(accepted)" || report 1 "repeated half refused"
phase_is 2 && report 1 "repeated mark does not join" || report 0 "repeated mark joined" "(phase=$(cat "$STATE/current_phase"))"
adv "web" && phase_is 4 && report 1 "second mark joins 2 -> 4" || report 0 "second mark joins" "(phase=$(cat "$STATE/current_phase"))"
[[ "$(rflags)" == "files=no web=no" ]] && report 1 "join clears both marks" || report 0 "join left marks behind" "($(rflags))"
# Order independence: web first, then files.
set_phase 1; clear_class
adv "standard"; adv "web"; phase_is 2 && report 1 "web-first mark keeps the phase at 2" || report 0 "web-first" "(phase=$(cat "$STATE/current_phase"))"
adv "files"; phase_is 4 && report 1 "files-second mark joins 2 -> 4" || report 0 "files-second join" "(phase=$(cat "$STATE/current_phase"))"
# An empty (hand-touched) flag is not a mark.
set_phase 2; set_class "standard"; rm -f "$STATE"/research_*.done; : > "$STATE/research_web.done"
adv "files"; phase_is 2 && report 1 "empty web flag does not count as a mark" || report 0 "empty flag counted" "(phase=$(cat "$STATE/current_phase"))"
adv "web"; phase_is 4 && report 1 "real web mark then joins" || report 0 "real web mark join" "(phase=$(cat "$STATE/current_phase"))"
# A half argument is refused everywhere else.
for p in 1 4 5 6 7 8; do
  set_phase "$p"; set_class "standard"
  adv "files" && report 0 "half argument refused at phase $p" "(accepted)" || report 1 "half argument refused at phase $p"
done
# Marks never survive a return to phase 1, whichever path takes it there.
set_phase 6; set_class "standard"; printf 'files finished x\n' > "$STATE/research_files.done"
rollback; [[ ! -f "$STATE/research_files.done" ]] && report 1 "rollback clears the marks" || report 0 "rollback left a mark"
set_phase 1; clear_class; printf 'files finished x\n' > "$STATE/research_files.done"
adv "standard"; [[ ! -f "$STATE/research_files.done" ]] && report 1 "entering the block starts it fresh" || report 0 "stale mark survived into the block"
set_phase 5; set_class "standard"; set_model 1; printf 'files finished x\n' > "$STATE/research_files.done"
adv; [[ ! -f "$STATE/research_files.done" ]] && report 1 "model migration clears the marks" || report 0 "migration left a mark"
set_phase 5; set_class "standard"

echo "== a missing class blocks everything past phase 1 =="
set_phase 5; clear_class
adv && report 0 "no class recorded blocks advance" "(advanced)" || report 1 "no class recorded blocks advance"

echo "== the full walks =="
set_phase 1; clear_class
adv "standard"                          # 1 -> 2
adv "files"; adv "web"                  # 2 -> 4 (join)
for _ in 1 2 3 4 5; do adv; done        # 4 -> 9
phase_is 9 && report 1 "standard walk reaches 9" || report 0 "standard walk" "(phase=$(cat "$STATE/current_phase"))"
adv && report 0 "advance refuses at the terminal phase" "(advanced)" || report 1 "advance refuses at the terminal phase"

set_phase 1; clear_class
adv "trivial"                           # 1 -> 5
for _ in 1 2 3 4; do adv; done          # 5 -> 9
phase_is 9 && report 1 "trivial walk reaches 9" || report 0 "trivial walk" "(phase=$(cat "$STATE/current_phase"))"

echo "== a running subagent never blocks a phase change (owner decision) =="
# A leftover subagent_count from the retired busy-counter model must be inert.
set_phase 5; set_class "standard"; echo 1 > "$STATE/subagent_count"
adv && report 1 "advance proceeds with a stale busy counter" || report 0 "advance proceeds with a stale busy counter" "(blocked)"
set_phase 6
rollback && report 1 "rollback proceeds with a stale busy counter" || report 0 "rollback proceeds with a stale busy counter" "(blocked)"
rm -f "$STATE/subagent_count"

echo "== rollback 6 -> 1 =="
set_phase 6; set_class "standard"
rollback; phase_is 1 && report 1 "rollback moves 6 -> 1" || report 0 "rollback moves 6 -> 1" "(phase=$(cat "$STATE/current_phase"))"
[[ ! -f "$STATE/task_class" ]] && report 1 "rollback clears the task class" || report 0 "rollback clears the class"
for p in 1 2 3 4 5 7 8 9; do
  set_phase "$p"; set_class "standard"
  rollback && report 0 "rollback refused at phase $p" "(rolled back)" || report 1 "rollback refused at phase $p"
done

echo "== sub-agents: the model is the first rule =="
# Root CLAUDE.md: "only opus sub-agents are allowed". Exactly one "model":"opus"
# passes at every phase and everything else is denied. Phase 2 additionally
# needs the research half in the description (covered on its own below), so the
# allow probe there is tagged. The ledger is reset per phase so that the slots
# these allowed spawns take do not run into the concurrency limit here.
set_class "standard"
for p in 1 2 3 4 5 6 7 8 9; do
  set_phase "$p"; ledger_reset
  expect_allow gate-subagent.sh "$(json_agent opus "files: read")" "phase $p allows an opus spawn"
  expect_deny  gate-subagent.sh "$(json_agent sonnet)" "phase $p denies a sonnet spawn"
  expect_deny  gate-subagent.sh "$(json_agent haiku)"  "phase $p denies a haiku spawn"
done
ledger_reset
# No class recorded and no phase file: the gate must not care.
set_phase 5; clear_class
expect_allow gate-subagent.sh "$(json_agent opus)"                                "opus allowed with no task class"
rm -f "$STATE/current_phase"
expect_allow gate-subagent.sh "$(json_agent opus)"                                "opus allowed with no phase file"
set_phase 5; set_class "standard"
task_mode_off
expect_allow gate-subagent.sh "$(json_agent opus)"                                "opus allowed with task mode off"
expect_deny  gate-subagent.sh "$(json_agent sonnet)"                              "sonnet denied with task mode off"
task_mode_on
expect_deny gate-subagent.sh "$(json_agent fable)"                                "fable denied"
expect_deny gate-subagent.sh "$(json_agent Opus)"                                 "wrong-case Opus denied"
expect_deny gate-subagent.sh "$(json_agent "opus ")"                              "padded 'opus ' denied"
expect_deny gate-subagent.sh "$(json_agent "")"                                   "empty model denied"
expect_deny gate-subagent.sh "$(json_agent claude-opus-5)"                        "full model id denied (only the alias is allowed)"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{"prompt":"p"}}'  "spawn with no model denied"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{"model":"opus","options":{"model":"opus"}}}' "duplicate model keys denied even when both are opus"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{"model":"opus","options":{"model":"sonnet"}}}' "decoy opus ahead of sonnet denied"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{"model":"sonnet","options":{"model":"opus"}}}' "decoy opus behind sonnet denied"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{}}'              "empty tool_input denied"
expect_deny gate-subagent.sh '{}'                                                 "malformed payload denied"
# An escaped decoy inside the prompt text is data, not a second model field.
expect_allow gate-subagent.sh '{"tool_name":"Agent","tool_use_id":"toolu_x1","tool_input":{"model":"opus","prompt":"say \"model\":\"sonnet\" back"}}' "escaped decoy in the prompt is ignored"
# Whitespace around the colon is still one field.
expect_allow gate-subagent.sh '{"tool_name":"Agent","tool_use_id":"toolu_x2","tool_input":{"model" : "opus","prompt":"p"}}' "spaced model field allowed"
# The library declares the allowed model once.
( source "$HOOKS/lib/guard-common.sh"
  [[ "$SUBAGENT_ALLOWED_MODEL" == "opus" ]] && printf 'SM_OK\n' || printf 'SM_FAIL [%s]\n' "$SUBAGENT_ALLOWED_MODEL"
) > "$SANDBOX/sm.out" 2>&1
grep -q SM_OK "$SANDBOX/sm.out" && report 1 "SUBAGENT_ALLOWED_MODEL is opus" || report 0 "SUBAGENT_ALLOWED_MODEL" "($(tr '\n' ' ' < "$SANDBOX/sm.out"))"

echo "== a spawn leaves no state behind =="
rm -f "$STATE/subagent_count" "$STATE/spawns_this_phase"
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
[[ ! -f "$STATE/subagent_count" ]] && report 1 "allowed spawn writes no counter" || report 0 "allowed spawn wrote a counter"
[[ ! -f "$STATE/spawns_this_phase" ]] && report 1 "allowed spawn writes no budget record" || report 0 "allowed spawn wrote a budget record"

echo "== MCP tools that spawn agents are not gated; file writers still are =="
for t in mcp__x__spawn_task mcp__x__create_agent mcp__x__start_session mcp__x__run_task mcp__x__agent_create mcp__x__delegate_work mcp__x__launch_worker mcp__x__dispatch_job; do
  expect_allow gate-mcp-write.sh "$(json_mcp "$t")" "mcp spawn allowed: $t"
done
for t in mcp__browser__read_page mcp__browser__form_input mcp__x__list_sessions mcp__x__get_task_status; do
  expect_allow gate-mcp-write.sh "$(json_mcp "$t")" "mcp non-spawn allowed: $t"
done
for t in mcp__x__write_file mcp__x__create_file mcp__x__upload_asset mcp__x__download_file mcp__x__save_document mcp__x__delete_attachment; do
  expect_deny gate-mcp-write.sh "$(json_mcp "$t")" "mcp file writer denied: $t"
done

echo "== the ledger hooks are wired; the retired integer counter is not =="
for h in track-subagent-start.sh track-subagent-stop.sh track-agent-result.sh; do
  [[ -f "$HOOKS/$h" ]] && report 1 "$h present" || report 0 "$h missing"
done
for ev in SubagentStart SubagentStop PostToolUseFailure; do
  grep -q "\"$ev\"" "$REPO_ROOT/.claude/settings.json" && report 1 "settings.json wires $ev" || report 0 "settings.json lacks $ev"
done
grep -q 'track-agent-result.sh' "$REPO_ROOT/.claude/settings.json" && report 1 "settings.json wires track-agent-result.sh" || report 0 "settings.json lacks track-agent-result.sh"
grep -q '"CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS": *"8"' "$REPO_ROOT/.claude/settings.json" && report 1 "settings.json caps native concurrency at 8" || report 0 "settings.json lacks the native cap"
grep -q '"CLAUDE_CODE_WORKFLOW_MAX_CONCURRENT_AGENTS": *"8"' "$REPO_ROOT/.claude/settings.json" && report 1 "settings.json caps workflow concurrency at 8" || report 0 "settings.json lacks the workflow cap"
grep -q "subagent_count" "$HOOKS/advance.sh" "$HOOKS/rollback.sh" "$HOOKS/gate-stop.sh" && report 0 "a phase script still reads subagent_count" || report 1 "no phase script reads subagent_count"

echo "== fail-closed when the guard library is missing =="
mv "$HOOKS/lib/guard-common.sh" "$HOOKS/lib/guard-common.sh.bak"
set_phase 5
expect_deny gate-bash.sh "$(json_cmd "ls")"                          "gate-bash denies without its library"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/Server/Program.cs")" "gate-edit denies without its library"
expect_deny gate-subagent.sh "$(json_agent opus)"                     "gate-subagent denies without its library"
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
set_phase 8; set_class "standard"
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
  set_phase 9; set_class "standard"
  out=$(printf '%s' "$(prompt_json "$q")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
  p=$(cat "$STATE/current_phase")
  [[ "$p" == "1" ]] && report 1 "resets 9 -> 1: $q" || report 0 "resets 9 -> 1: $q" "(phase=$p)"
  [[ ! -f "$STATE/task_class" ]] && report 1 "class cleared for: $q" || report 0 "class cleared for: $q"
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

echo "== SessionStart preserves state on compact/resume =="
set_phase 5
printf '{"session_id":"s1","source":"compact"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "compact does not reset the phase" || report 0 "compact does not reset" "(phase=$p)"

set_phase 5
printf '{"session_id":"s1","source":"resume"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "resume does not reset the phase" || report 0 "resume does not reset" "(phase=$p)"

# The case actually observed: a SessionStart arriving mid-task reporting
# source=startup with a new id.
set_phase 7; rm -f "$STATE/last_session_id"
printf '{"session_id":"s7","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "7" ]] && report 1 "mid-task startup does not reset a working phase" || report 0 "mid-task startup preserved" "(phase=$p)"

set_phase 8; rm -f "$STATE/last_session_id"
printf '{"session_id":"s8","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "8" ]] && report 1 "phase 8 counts as a working phase" || report 0 "phase 8 preserved" "(phase=$p)"

set_phase 5
printf '%s' "s1" > "$STATE/last_session_id"
printf '{"session_id":"s1","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "5" ]] && report 1 "same session id re-fire does not reset" || report 0 "same id preserved" "(phase=$p)"

# Resting phases stay resettable, including after the 30-minute window.
set_phase 9; set_class "standard"; rm -f "$STATE/last_session_id"
echo 3 > "$STATE/subagent_count"; printf '5:2' > "$STATE/spawns_this_phase"
printf '{"session_id":"s2","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "genuine startup at a resting phase resets to 1" || report 0 "startup resets" "(phase=$p)"
[[ ! -f "$STATE/task_class" ]] && report 1 "session reset clears the task class" || report 0 "session reset clears the class"
[[ ! -f "$STATE/subagent_count" && ! -f "$STATE/spawns_this_phase" ]] && report 1 "session reset removes retired counter files" || report 0 "retired counter files survived the reset"

set_phase 5; touch -d '2 hours ago' "$STATE/current_phase" 2>/dev/null
rm -f "$STATE/last_session_id"
printf '{"session_id":"s3","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "stale working phase (>30 min) is reset" || report 0 "stale phase reset" "(phase=$p)"

[[ "$(cat "$STATE/last_session_id" 2>/dev/null)" == "s3" ]] && report 1 "session id recorded for re-fire detection" || report 0 "session id recorded"

echo "== phase model migration: a number from a retired model is never trusted =="
# A stale stamp must override EVERY preserve signal. Phase 5 with a fresh mtime
# and source=compact is the strongest preserve case there is.
set_phase 5; set_class "standard"
set_model 1
printf '%s' "s1" > "$STATE/last_session_id"
out=$(printf '{"session_id":"s1","source":"compact"}' | bash "$HOOKS/session-start.sh" 2>/dev/null)
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "stale model forces a reset even on compact" || report 0 "stale model reset" "(phase=$p)"
[[ ! -f "$STATE/task_class" ]] && report 1 "stale model clears the task class" || report 0 "stale model clears the class"
[[ "$(cat "$STATE/phase_model")" == "$MODEL_VERSION" ]] && report 1 "session-start restamps the model version" || report 0 "session-start restamps"
printf '%s' "$out" | grep -q "retired model\|different model" && report 1 "migration is announced, not silent" || report 0 "migration announced"

# advance.sh and rollback.sh normalise rather than act on an uninterpretable number.
set_phase 5; set_class "standard"; set_model 1
adv && report 0 "advance refuses under a stale model" "(advanced)" || report 1 "advance refuses under a stale model"
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "advance normalises the phase to 1" || report 0 "advance normalises" "(phase=$p)"
[[ "$(cat "$STATE/phase_model")" == "$MODEL_VERSION" ]] && report 1 "advance restamps the model version" || report 0 "advance restamps"

set_phase 6; set_class "standard"; set_model 1
rollback && report 0 "rollback refuses under a stale model" "(rolled back)" || report 1 "rollback refuses under a stale model"
p=$(cat "$STATE/current_phase")
[[ "$p" == "1" ]] && report 1 "rollback normalises the phase to 1" || report 0 "rollback normalises" "(phase=$p)"

# An unstamped state directory is treated the same way as a stale one.
set_phase 5; set_class "standard"; rm -f "$STATE/phase_model"
adv && report 0 "advance refuses with no model stamp" "(advanced)" || report 1 "advance refuses with no model stamp"
[[ "$(cat "$STATE/current_phase")" == "1" ]] && report 1 "unstamped state normalises to phase 1" || report 0 "unstamped normalises"
set_phase 5; set_class "standard"

echo "== Stop gate =="
task_mode_on; set_class "standard"
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
printf '%s' "$out" | grep -q '"decision":"block"' && report 0 "Stop ignores a stale busy counter" "(blocked)" || report 1 "Stop ignores a stale busy counter"
rm -f "$STATE/subagent_count"

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
set_phase 8; set_class "standard"
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
expect_allow gate-edit.sh "$(json_write "$SANDBOX/.claude/state/edits.log")"          "unlocked .claude/state files stay writable"

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

echo "== the injected rules carry the sharing guidance =="
out=$(printf '{"session_id":"g0","source":"startup"}' | bash "$HOOKS/session-start.sh" 2>/dev/null)
printf '%s' "$out" | grep -q "Do not quote the passphrase on your own initiative" && report 1 "rules say: not on own initiative" || report 0 "rules lack the own-initiative wording"
printf '%s' "$out" | grep -q "When the user asks for the phrase, quote" && report 1 "rules say: quote on request" || report 0 "rules lack the on-request wording"
printf '%s' "$out" | grep -qF "$PASSPHRASE" && report 0 "session-start leaks the passphrase into the rules" || report 1 "session-start does not leak the passphrase"
printf '%s' "$out" | grep -q "only model 'opus'\|equal to 'opus'" && report 1 "rules state the opus-only subagent policy" || report 0 "rules lack the opus-only policy"
printf '%s' "$out" | grep -qi "SUB-AGENTS ARE NOT ALLOWED" && report 0 "rules still forbid subagents" || report 1 "rules no longer forbid subagents"
set_phase 5; set_class "standard"
out=$(printf '%s' "$(prompt_json "please update the gate")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
printf '%s' "$out" | grep -q "only model 'opus' may be spawned" && report 1 "prompt reminder states the opus-only policy" || report 0 "prompt reminder lacks the opus-only policy"
printf '%s' "$out" | grep -q "NOT allowed at any phase" && report 0 "prompt reminder still forbids subagents" || report 1 "prompt reminder no longer forbids subagents"

echo "== the grant expires when the turn ends =="
set_phase 9; set_class "standard"; grant_on
: > "$STATE/rebuild_pending.log"
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

echo "== sub-agent concurrency: 8 slots, the 9th spawn is denied =="
# The ledger is a set of slot files taken with an exclusive create. Eight
# allowed spawns fill it; the ninth is denied at once (no waiting), and the
# deny reason names the limit. Helpers feed the tracking hooks the same shapes
# the runtime was observed to send (tool_use_id, agentId, status, agent_id).
json_post()  { printf '{"hook_event_name":"PostToolUse","tool_name":"Agent","tool_use_id":"%s","tool_input":{"model":"opus","prompt":"p"},"tool_response":{"status":"%s","agentId":"%s"}}' "$1" "$2" "$3"; }
json_fail()  { printf '{"hook_event_name":"PostToolUseFailure","tool_name":"Agent","tool_use_id":"%s","tool_input":{"model":"opus","prompt":"p"},"error":"boom"}' "$1"; }
json_start() { printf '{"hook_event_name":"SubagentStart","agent_id":"%s","agent_type":"general-purpose"}' "$1"; }
json_stop()  { printf '{"hook_event_name":"SubagentStop","stop_hook_active":false,"agent_id":"%s","agent_type":"general-purpose","agent_transcript_path":"x"}' "$1"; }
last_res_id() { ls "$STATE/agents"/res.* 2>/dev/null | sed 's/.*res\.toolu_//' | sort -n | tail -n 1 | sed 's/^/toolu_/'; }
set_phase 5; set_class "standard"; task_mode_on; ledger_reset
for i in 1 2 3 4 5 6 7 8; do
  expect_allow gate-subagent.sh "$(json_agent opus)" "spawn $i of 8 allowed"
done
[[ "$(slots_held)" == "8" ]] && report 1 "eight slots held" || report 0 "eight slots held" "(held=$(slots_held))"
expect_deny gate-subagent.sh "$(json_agent opus)" "9th spawn denied"
out=$(printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" 2>/dev/null)
printf '%s' "$out" | grep -q "8 sub-agents are already running" && report 1 "deny reason names the limit" || report 0 "deny reason names the limit" "($out)"
[[ "$(slots_held)" == "8" ]] && report 1 "a denied spawn takes no slot" || report 0 "denied spawn took a slot" "(held=$(slots_held))"
# The limit holds at every phase, task mode on or off.
task_mode_off
expect_deny gate-subagent.sh "$(json_agent opus)" "9th spawn denied with task mode off"
task_mode_on
for p in 1 4 9; do set_phase "$p"; expect_deny gate-subagent.sh "$(json_agent opus "files: x")" "9th spawn denied at phase $p"; done
set_phase 5

echo "== a slot is released when its agent stops, through the link =="
# Background spawn: PostToolUse reports status async_launched + agentId, which
# binds the reservation to the agent; SubagentStop for that agent frees it.
ledger_reset
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
RID=$(last_res_id)
[[ -n "$RID" && "$(slots_held)" == "1" ]] && report 1 "reservation recorded under the tool_use_id" || report 0 "reservation recorded" "(rid=$RID held=$(slots_held))"
printf '%s' "$(json_post "$RID" async_launched agentA)" | bash "$HOOKS/track-agent-result.sh" >/dev/null 2>&1
[[ -f "$STATE/agents/agent.agentA" ]] && report 1 "async_launched links the slot to the agent id" || report 0 "link missing"
printf '%s' "$(json_start agentA)" | bash "$HOOKS/track-subagent-start.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "1" ]] && report 1 "SubagentStart takes no second slot" || report 0 "SubagentStart double-counted" "(held=$(slots_held))"
printf '%s' "$(json_stop agentZ)" | bash "$HOOKS/track-subagent-stop.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "1" ]] && report 1 "SubagentStop of an unknown agent releases nothing" || report 0 "unknown agent released a slot" "(held=$(slots_held))"
out=$(printf '%s' "$(json_stop agentA)" | bash "$HOOKS/track-subagent-stop.sh" 2>/dev/null); rc=$?
[[ "$rc" == "0" ]] && ! printf '%s' "$out" | grep -q '"decision"' && report 1 "SubagentStop never blocks" || report 0 "SubagentStop blocked" "(rc=$rc out=$out)"
[[ "$(slots_held)" == "0" && ! -f "$STATE/agents/agent.agentA" && ! -f "$STATE/agents/res.$RID" ]] && report 1 "SubagentStop releases slot, link and reservation" || report 0 "release incomplete" "($(ls "$STATE/agents"))"
printf '%s' "$(json_stop agentA)" | bash "$HOOKS/track-subagent-stop.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "0" ]] && report 1 "a repeated SubagentStop is idempotent" || report 0 "repeated stop broke the ledger"
# Foreground spawn: the tool returns after the agent finished (status
# completed), so PostToolUse releases directly - SubagentStop already ran.
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
RID=$(last_res_id)
printf '%s' "$(json_stop agentB)" | bash "$HOOKS/track-subagent-stop.sh" >/dev/null 2>&1
printf '%s' "$(json_post "$RID" completed agentB)" | bash "$HOOKS/track-agent-result.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "0" ]] && report 1 "completed status releases the slot at PostToolUse" || report 0 "completed status left a slot" "(held=$(slots_held))"
# A spawn that failed never produced an agent: PostToolUseFailure releases.
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
RID=$(last_res_id)
printf '%s' "$(json_fail "$RID")" | bash "$HOOKS/track-agent-result.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "0" ]] && report 1 "PostToolUseFailure releases the reservation" || report 0 "failure left a slot" "(held=$(slots_held))"
# After a release the pool admits again.
for i in 1 2 3 4 5 6 7 8; do printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1; done
RID=$(last_res_id)
expect_deny gate-subagent.sh "$(json_agent opus)" "full pool denies"
printf '%s' "$(json_post "$RID" async_launched agentC)" | bash "$HOOKS/track-agent-result.sh" >/dev/null 2>&1
printf '%s' "$(json_stop agentC)" | bash "$HOOKS/track-subagent-stop.sh" >/dev/null 2>&1
expect_allow gate-subagent.sh "$(json_agent opus)" "one release admits one more spawn"
expect_deny  gate-subagent.sh "$(json_agent opus)" "and the pool is full again"
ledger_reset

echo "== a spawn the runtime never confirmed is reclaimed by age =="
ledger_reset
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
RID=$(last_res_id)
touch -d '10 minutes ago' "$STATE/agents/res.$RID" 2>/dev/null
for i in 1 2 3 4 5 6 7; do printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1; done
expect_allow gate-subagent.sh "$(json_agent opus)" "an unlinked reservation older than the TTL is reaped, freeing its slot"
# A linked slot never ages out, however old.
ledger_reset
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
RID=$(last_res_id)
printf '%s' "$(json_post "$RID" async_launched agentOld)" | bash "$HOOKS/track-agent-result.sh" >/dev/null 2>&1
touch -d '3 hours ago' "$STATE/agents/res.$RID" "$STATE/agents/slot.1" 2>/dev/null
for i in 1 2 3 4 5 6 7; do printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1; done
expect_deny gate-subagent.sh "$(json_agent opus)" "a linked slot is never reaped by age"
ledger_reset

echo "== a killed sub-agent fires no SubagentStop: the task notification releases it =="
# Observed live: TaskStop emits no SubagentStop, only a task-notification whose
# <task-id> is the agent id. inject-phases.sh settles the ledger from it.
ledger_reset; set_phase 5; set_class "standard"
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
RID=$(last_res_id)
printf '%s' "$(json_post "$RID" async_launched agentK)" | bash "$HOOKS/track-agent-result.sh" >/dev/null 2>&1
printf '%s' "$(prompt_json "<task-notification>\\n<task-id>agentK</task-id>\\n<tool-use-id>${RID}</tool-use-id>\\n<status>killed</status>\\n</task-notification>")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "0" ]] && report 1 "kill notification releases the linked slot" || report 0 "kill notification left the slot" "(held=$(slots_held))"
[[ "$(cat "$STATE/current_phase")" == "5" ]] && report 1 "kill notification still moves no phase" || report 0 "kill notification moved the phase"
# Not yet linked (PostToolUse never ran): the tool-use-id releases the reservation.
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
RID=$(last_res_id)
printf '%s' "$(prompt_json "<task-notification>\\n<task-id>agentQ</task-id>\\n<tool-use-id>${RID}</tool-use-id>\\n<status>killed</status>\\n</task-notification>")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "0" ]] && report 1 "kill notification releases an unlinked reservation by tool-use-id" || report 0 "unlinked reservation survived the kill notification" "(held=$(slots_held))"
# A notification for a plain background shell touches nothing.
printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
printf '%s' "$(prompt_json "<task-notification>\\n<task-id>b03s4nyee</task-id>\\n<status>completed</status>\\n</task-notification>")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
[[ "$(slots_held)" == "1" ]] && report 1 "an unrelated task notification releases nothing" || report 0 "unrelated notification touched the ledger" "(held=$(slots_held))"
ledger_reset

echo "== a burst of parallel spawns admits exactly 8 =="
# Claude Code runs the hooks of parallel tool calls concurrently. Twelve gate
# processes started at once on an empty ledger must end with eight slots held
# and four denies - the exclusive create is what makes that exact.
ledger_reset
for i in $(seq 1 12); do
  ( printf '%s' "$(json_agent opus)" | bash "$HOOKS/gate-subagent.sh" 2>/dev/null | grep -q '"deny"' && echo deny || echo allow ) > "$SANDBOX/burst.$i" &
done
wait
ALLOWS=$(cat "$SANDBOX"/burst.* | grep -c allow); DENIES=$(cat "$SANDBOX"/burst.* | grep -c deny)
[[ "$ALLOWS" == "8" && "$DENIES" == "4" && "$(slots_held)" == "8" ]] && report 1 "burst of 12: 8 allowed, 4 denied, 8 slots held" || report 0 "burst admission" "(allow=$ALLOWS deny=$DENIES held=$(slots_held))"
rm -f "$SANDBOX"/burst.*; ledger_reset

echo "== a payload the ledger cannot track is denied =="
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_input":{"model":"opus","prompt":"p"}}' "spawn without tool_use_id denied"
expect_deny gate-subagent.sh '{"tool_name":"Agent","tool_use_id":"../x","tool_input":{"model":"opus","prompt":"p"}}' "spawn with a path-shaped tool_use_id denied"
[[ "$(slots_held)" == "0" ]] && report 1 "untrackable spawns take no slot" || report 0 "untrackable spawn took a slot"

echo "== research block: every spawn names its half, each half has a budget =="
set_phase 2; set_class "standard"; task_mode_on; ledger_reset; rm -f "$STATE"/research_*.done
expect_deny  gate-subagent.sh "$(json_agent opus "read the hooks")"      "phase 2 denies an untagged spawn"
expect_deny  gate-subagent.sh "$(json_agent opus "Files: read")"         "phase 2 denies a wrong-case tag"
expect_deny  gate-subagent.sh "$(json_agent opus "files read")"          "phase 2 denies a tag without the colon"
expect_deny  gate-subagent.sh "$(json_agent opus " files: read")"        "phase 2 denies a tag not at the start"
[[ "$(slots_held)" == "0" ]] && report 1 "tag denials take no slot" || report 0 "tag denial took a slot"
for i in 1 2 3 4 5; do expect_allow gate-subagent.sh "$(json_agent opus "files: reader $i")" "files spawn $i of 5 allowed"; done
expect_deny gate-subagent.sh "$(json_agent opus "files: reader 6")" "6th files spawn denied (budget 5)"
[[ "$(slots_held)" == "5" ]] && report 1 "a budget denial gives its slot back" || report 0 "budget denial kept a slot" "(held=$(slots_held))"
for i in 1 2 3; do expect_allow gate-subagent.sh "$(json_agent opus "web: searcher $i")" "web spawn $i of 3 allowed"; done
expect_deny gate-subagent.sh "$(json_agent opus "web: searcher 4")" "4th web spawn denied (budget 3)"
[[ "$(slots_held)" == "8" ]] && report 1 "5 + 3 research spawns fill the 8 slots exactly" || report 0 "research spawns vs slots" "(held=$(slots_held))"
# The budget is per block: finished agents free slots, not budget.
for f in "$STATE/agents"/res.*; do rid=${f##*/res.}; printf '%s' "$(json_post "$rid" async_launched "ag$rid")" | bash "$HOOKS/track-agent-result.sh" >/dev/null 2>&1; printf '%s' "$(json_stop "ag$rid")" | bash "$HOOKS/track-subagent-stop.sh" >/dev/null 2>&1; done
[[ "$(slots_held)" == "0" ]] && report 1 "all research agents released their slots" || report 0 "research slots not released" "(held=$(slots_held))"
expect_deny gate-subagent.sh "$(json_agent opus "files: reader 6")" "files budget stays spent after the agents finished"
expect_deny gate-subagent.sh "$(json_agent opus "web: searcher 4")"  "web budget stays spent after the agents finished"
# Leaving the block clears the budget; entering it again starts fresh.
adv "files"; adv "web"
phase_is 4 && report 1 "block joined into 4 with budgets spent" || report 0 "join with budgets" "(phase=$(cat "$STATE/current_phase"))"
[[ -z "$(ls "$STATE/agents" 2>/dev/null | grep '^budget\.')" ]] && report 1 "join clears the budgets" || report 0 "join left budget files"
set_phase 1; clear_class; adv "standard"
expect_allow gate-subagent.sh "$(json_agent opus "files: fresh")" "a fresh block has a fresh budget"
# Rollback and the terminal-phase reset clear budgets too.
set_phase 6; set_class "standard"; rollback
[[ -z "$(ls "$STATE/agents" 2>/dev/null | grep '^budget\.')" ]] && report 1 "rollback clears the budgets" || report 0 "rollback left budget files"
set_phase 2; set_class "standard"; printf '%s' "$(json_agent opus "web: w")" | bash "$HOOKS/gate-subagent.sh" >/dev/null 2>&1
set_phase 9
printf '%s' "$(prompt_json "next task please")" | bash "$HOOKS/inject-phases.sh" >/dev/null 2>&1
[[ -z "$(ls "$STATE/agents" 2>/dev/null | grep '^budget\.')" ]] && report 1 "the 9 -> 1 reset clears the budgets" || report 0 "reset left budget files"
[[ ! -f "$STATE/research_files.done" && ! -f "$STATE/research_web.done" ]] && report 1 "the 9 -> 1 reset clears the marks" || report 0 "reset left marks"
ledger_reset
# Outside the block, and with task mode off, no tag is needed.
set_phase 5; set_class "standard"
expect_allow gate-subagent.sh "$(json_agent opus "read the hooks")" "phase 5 needs no tag"
task_mode_off; set_phase 2
expect_allow gate-subagent.sh "$(json_agent opus "read the hooks")" "task mode off: phase 2 needs no tag"
task_mode_on; set_phase 5; ledger_reset

echo "== the marks and the ledger are machine-owned =="
set_phase 5; set_class "standard"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/research_files.done")"  "Write onto research_files.done denied"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/research_web.done")"    "Write onto research_web.done denied"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/agents/slot.1")"        "Write onto a slot file denied"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/agents/budget.web.3")"  "Write onto a budget file denied"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/AGENTS/Slot.1")"        "wrong-case ledger path denied"
expect_deny gate-bash.sh "$(json_cmd "touch .claude/state/research_web.done")"          "touch research_web.done denied"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/research_files.done")"     "redirect onto research_files.done denied"
expect_deny gate-bash.sh "$(json_cmd "rm .claude/state/agents/slot.1")"                 "deleting a slot file denied"
expect_deny gate-bash.sh "$(json_cmd "rm -rf .claude/state/agents")"                    "deleting the ledger denied"
expect_deny gate-bash.sh "$(json_cmd "rm -f .claude/state/agents/budget.files.5")"      "deleting a budget file denied"
expect_deny gate-bash.sh "$(json_cmd "mv .claude/state/agents .claude/state/old")"      "moving the ledger denied"
expect_deny gate-bash.sh "$(json_cmd "echo x > .claude/state/../state/agents/slot.2")"  "traversal onto the ledger denied"
task_mode_on; set_phase 5
# The grant covers the enforcement layer only, never the ledger.
: > "$STATE/hook_edit_grant"
expect_deny gate-edit.sh "$(json_write "$SANDBOX/.claude/state/agents/slot.1")"        "grant does NOT unlock the ledger"
rm -f "$STATE/hook_edit_grant"
expect_allow gate-edit.sh "$(json_write "$SANDBOX/.claude/state/subagent_events.log")"   "the event log stays writable"

echo "== SessionStart clears the ledger on a process restart, keeps it on compact =="
set_phase 5; set_class "standard"
mkdir -p "$STATE/agents"; printf 'x\n' > "$STATE/agents/slot.1"
printf '{"session_id":"L1","source":"compact"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ -f "$STATE/agents/slot.1" ]] && report 1 "compact keeps the ledger" || report 0 "compact cleared the ledger"
printf '%s' "L1" > "$STATE/last_session_id"
printf '{"session_id":"L1","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ -f "$STATE/agents/slot.1" ]] && report 1 "same-session re-fire keeps the ledger" || report 0 "same-session re-fire cleared the ledger"
printf '{"session_id":"L2","source":"resume"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ ! -d "$STATE/agents" ]] && report 1 "resume clears the ledger" || report 0 "resume kept the ledger"
mkdir -p "$STATE/agents"; printf 'x\n' > "$STATE/agents/slot.1"
printf '{"session_id":"L3","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ ! -d "$STATE/agents" ]] && report 1 "startup clears the ledger" || report 0 "startup kept the ledger"
mkdir -p "$STATE/agents"; printf 'x\n' > "$STATE/agents/slot.1"
printf '{"session_id":"L4","source":"clear"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ ! -d "$STATE/agents" ]] && report 1 "clear clears the ledger" || report 0 "clear kept the ledger"
set_phase 9; set_class "standard"; rm -f "$STATE/last_session_id"; printf 'files finished x\n' > "$STATE/research_files.done"
printf '{"session_id":"L5","source":"startup"}' | bash "$HOOKS/session-start.sh" >/dev/null 2>&1
[[ ! -f "$STATE/research_files.done" ]] && report 1 "session reset clears the marks" || report 0 "session reset left a mark"
set_phase 5; set_class "standard"

echo "== the injected rules describe the block and the limits =="
out=$(printf '{"session_id":"r0","source":"startup"}' | bash "$HOOKS/session-start.sh" 2>/dev/null)
printf '%s' "$out" | grep -q "PHASE 2 IS A FORK/JOIN" && report 1 "rules describe the fork/join" || report 0 "rules lack the fork/join"
printf '%s' "$out" | grep -q 'advance.sh \\"files\\"' && report 1 "rules give the files mark" || report 0 "rules lack the files mark"
printf '%s' "$out" | grep -q 'advance.sh \\"web\\"' && report 1 "rules give the web mark" || report 0 "rules lack the web mark"
printf '%s' "$out" | grep -q "at most 8 sub-agents running at the same time" && report 1 "rules state the concurrency limit" || report 0 "rules lack the concurrency limit"
printf '%s' "$out" | grep -q "5 files agents, 3 web agents" && report 1 "rules state the block budgets" || report 0 "rules lack the block budgets"
printf '%s' "$out" | grep -q "PHASE MODEL VERSION 3" && report 1 "rules state model version 3" || report 0 "rules lack model version 3"
set_phase 5; set_class "standard"
out=$(printf '%s' "$(prompt_json "please update the gate")" | bash "$HOOKS/inject-phases.sh" 2>/dev/null)
printf '%s' "$out" | grep -q "at most 8 run at the same time" && report 1 "prompt reminder states the concurrency limit" || report 0 "prompt reminder lacks the concurrency limit"
printf '%s' "$out" | grep -q "research block" && report 1 "prompt reminder describes the block" || report 0 "prompt reminder lacks the block"
out=$(printf '{"stop_hook_active":false}' | bash "$HOOKS/gate-stop.sh" 2>/dev/null)
printf '%s' "$out" | grep -q "once per finished half" && report 1 "Stop reason explains the phase-2 marks" || report 0 "Stop reason lacks the marks"
ledger_reset; set_phase 5; set_class "standard"

echo
printf 'passed: %d   failed: %d\n' "$PASS" "$FAIL"
[[ "$FAIL" == "0" ]]
