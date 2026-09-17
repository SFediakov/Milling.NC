#!/usr/bin/env bash
# PreToolUse on Bash|PowerShell:
#   - Block destructive commands in both POSIX and PowerShell/cmd forms.
#   - Block destruction of protected assets (same asset list the Edit/Write
#     guard uses - see lib/guard-common.sh).
#   - Block any shell write to the root CLAUDE.md (user-owned), to the
#     phase-state files (current_phase, task_class, TASK_MODE), and to the
#     enforcement layer itself (settings.json, .claude/hooks/**).
#   - Block git operations that destroy local history or the worktree.
#   - Enforce CLAUDE.md rule: never `git pull --rebase`.
#   - Permanently reject `git merge ... main`; PR review/merge happens on GitHub.
#   - Block force-push (any target).
#   - Remote main is PR-only, at every phase, task mode or not: deny every
#     `git push` whose target is main in any refspec spelling, every push that
#     sweeps all branches (--all, --mirror, --branches, a glob), and every push
#     whose target the hook cannot read from the command (no refspec, or HEAD),
#     because git would resolve those from push.default and the upstream.
#     `git push origin <branch>` with a named non-main branch is the one form
#     that passes.
#   - Deny the explicit spellings that rewrite LOCAL main other than a
#     fast-forward sync: a fetch/pull refspec into main, branch -f/-M/-d main,
#     update-ref on main, checkout -B / switch -C main.
#   - In task mode, block file mutation from the shell during every read-only
#     phase (1-4 and 8). Without this the phase gate is trivially bypassed:
#     `sed -i` and `>` write files without ever touching the Edit/Write tools.
#   - In task mode phase 8, block shell execution entirely (report-only phase),
#     whitelisting advance.sh, rollback.sh and `gh pr create --base main`.
#
# Scanning model (this is the part that is easy to get wrong):
#   The command is split on `;`, `&&`, `||`, `|` and newlines. Each segment is
#   scanned separately. Quoted spans are stripped ONLY when the segment's first
#   token is a known read-only reader (grep, cat, echo, git log, ...) and the
#   segment contains no command substitution or -exec. That kills the false
#   positive on `grep -rn 'rm -rf /' logs/` without opening the classic hole:
#   interpreters and wrappers (bash -c, powershell -Command, sudo, xargs, env,
#   find -exec) are never quote-stripped, so their payload is still scanned.
#
#   WRITE TARGETS are extracted from a differently prepared text: quote
#   CHARACTERS are removed but quoted spans are kept, so `echo x > "CLAUDE.md"`
#   is still seen as a write to CLAUDE.md. The exception is a message-bearing
#   git segment (`git commit -m "... > CLAUDE.md ..."`), where the quoted span
#   really is inert prose and is stripped whole.
#
#   Segments that invoke an inline interpreter (`bash -c "echo x > f"`) are
#   re-scanned with quote characters removed, because there the quoted span is
#   a payload, not data. Probing found that case escaping the read-only gate.
#
#   This layer is a tripwire, not a security boundary. A regex denylist cannot
#   survive a determined bypass, and an interpreter handed a SCRIPT FILE rather
#   than an inline string is out of reach entirely. It exists to stop accidents,
#   which is the actual failure mode here.
#
# jq-free: uses sed/grep for JSON parsing and printf for JSON output.
set -uo pipefail

trap 'rc=$?; if [[ $rc -ne 0 ]]; then printf "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"gate-bash aborted before reaching a decision (exit $rc). Failing closed.\"}}\n"; fi' EXIT

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
CMD=$(json_unescape "$(hook_json_string "$INPUT" command)")

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

# --- segment preparation -----------------------------------------------------

segment_first_token() {
  local t
  t=$(printf '%s' "$1" | sed -E 's/^[[:space:]$({`]*//; s/[[:space:]].*$//')
  t=${t##*/}
  t=${t##*\\}
  lower "$t"
}

segment_second_token() {
  local t
  t=$(printf '%s' "$1" | sed -E 's/^[[:space:]$({`]*//; s/^[^[:space:]]+[[:space:]]+//; s/[[:space:]].*$//')
  lower "$t"
}

# True when quoted spans in this segment are inert DATA rather than a payload
# that could be executed - the search text of a grep, the message of a commit.
# Interpreters and wrappers are deliberately absent: bash, sh, pwsh, powershell,
# cmd, env, sudo, xargs, find, eval, ssh, awk and sed can all execute what they
# are handed in quotes. Command substitution anywhere in the segment disqualifies
# it outright, which is what stops `git commit -m "$(rm -rf .)"`.
quotes_are_inert() {
  local seg="$1" first second
  if printf '%s' "$seg" | grep -qE '(\$\(|`|--?exec(dir)?\b)'; then
    return 1
  fi
  first=$(segment_first_token "$seg")
  case "$first" in
    grep|rg|egrep|fgrep|zgrep|cat|head|tail|less|more|jq|echo|printf|wc|sort|uniq|ls|dir|stat|file|select-string|get-content|write-output|write-host)
      return 0
      ;;
    git)
      second=$(segment_second_token "$seg")
      case "$second" in
        log|diff|show|status|grep|blame|commit|tag) return 0 ;;
      esac
      ;;
  esac
  return 1
}

prepare_segment() {
  local seg="$1"
  if quotes_are_inert "$seg"; then
    printf '%s' "$seg" | sed -E "s/'[^']*'//g; s/\"[^\"]*\"//g"
  else
    printf '%s' "$seg"
  fi
}

extract_targets() {
  printf '%s' "$1" | tr -s '[:space:]' '\n' | grep -v '^$' | tail -n +2 \
    | grep -vE '^-' | grep -vE '^/[a-zA-Z]$'
}

is_ephemeral_target() {
  printf '%s' "$1" | grep -qiE "$EPHEMERAL_TARGET_REGEX" && return 0
  printf '%s' "$1" | grep -qiE "$SCRATCHPAD_TARGET_REGEX" && return 0
  return 1
}

match() { printf '%s' "$1" | grep -qiE "$2"; }

# --- git argument parsing ----------------------------------------------------
# The main-protection rules below cannot be flat regexes: a push target is the
# text after the LAST colon of a refspec, an option can sit anywhere, and git's
# own global options (-C <dir>, -c <k=v>, --git-dir=...) can separate `git` from
# its verb. Tokens come from `read -ra`, never from an unquoted expansion, so a
# glob like refs/heads/* is not expanded against the working directory. Quote
# characters are stripped from every token: `git push origin 'refs/heads/*'`
# must yield the same tokens as the unquoted form.

# Prints the arguments of git verb $2 in segment $1, one per line, and returns
# 0. Returns 1 (nothing printed) when the segment does not run that verb. The
# first token that is `git` (any path prefix, .exe, a leading $(, ( or `)
# starts the scan, so `sudo git`, `env git` and `bash -c "git ..."` are read.
git_verb_args() {
  local seg="$1" verb="$2" tok clean i n state=before
  local -a toks
  IFS=$' \t' read -ra toks <<< "$seg"
  n=${#toks[@]}
  for (( i = 0; i < n; i++ )); do
    tok=${toks[$i]//[\"\']/}
    [[ -z "$tok" ]] && continue
    case "$state" in
      before)
        clean=$(lower "${tok##*/}"); clean=${clean##*\\}
        clean=$(printf '%s' "$clean" | sed -E 's/^[$({`]+//; s/\.exe$//')
        [[ "$clean" == "git" ]] && state=globals
        ;;
      globals)
        case "$tok" in
          -C|-c|--git-dir|--work-tree|--namespace|--exec-path|--super-prefix)
            (( i++ )) ;;
          -*) ;;
          *)
            [[ "$(lower "$tok")" != "$verb" ]] && return 1
            state=args ;;
        esac
        ;;
      args)
        printf '%s\n' "$tok"
        ;;
    esac
  done
  [[ "$state" == "args" ]]
}

# Fills the array ARGS with the arguments of git verb $2 in segment $1. Returns
# 1 when the segment does not run that verb. A process substitution would hide
# the function's status behind mapfile's, hence the intermediate string.
ARGS=()
git_args_into() {
  local out
  out=$(git_verb_args "$1" "$2") || return 1
  ARGS=()
  [[ -n "$out" ]] && mapfile -t ARGS <<< "$out"
  return 0
}

# Refs that name main on either side: main, heads/main, refs/heads/main.
is_main_ref() {
  [[ "$(lower "$1")" =~ ^(refs/heads/|heads/)?main$ ]]
}

# Remote main is PR-only. One rule, applied to every push, at every phase:
# the command itself must show a named non-main branch as the target. A push
# with no refspec, or with HEAD as the source and no destination, is resolved
# by git from push.default and the upstream - the hook cannot see that, so it
# is denied rather than guessed. --all/--mirror/--branches and a glob sweep
# main in with everything else.
check_push_target() {
  local seg="$1" tok src dst i opts_done=0 remote="" refspecs=0
  git_args_into "$seg" push || return 0
  local -a args=("${ARGS[@]+"${ARGS[@]}"}")
  for (( i = 0; i < ${#args[@]}; i++ )); do
    tok=${args[$i]}
    if (( ! opts_done )); then
      case "$tok" in
        --) opts_done=1; continue ;;
        --all|--mirror|--branches)
          deny_pretooluse "'git push ${tok}' pushes every branch, main included. Remote main changes only through a PR the user merges. Push one named branch: git push origin <branch>." ;;
        -o|--push-option|--receive-pack|--exec|--repo)
          (( i++ )); continue ;;
        -*) continue ;;
      esac
    fi
    if [[ -z "$remote" ]]; then
      remote=$tok
      continue
    fi
    refspecs=$((refspecs + 1))
    tok=${tok#+}
    if [[ "$tok" == *:* ]]; then
      src=${tok%:*}; dst=${tok##*:}
      [[ -z "$dst" ]] && dst=$src
    else
      src=$tok; dst=$tok
      if [[ "$(lower "$src")" =~ ^(head|@)([^a-z0-9_/-]|$) ]]; then
        deny_pretooluse "'git push ... ${args[$i]}' lets git resolve the target branch from HEAD; the hook cannot verify it is not main. Name the branch: git push origin <branch>."
      fi
    fi
    if [[ "$dst" == *'*'* ]]; then
      deny_pretooluse "A glob refspec ('${args[$i]}') can update main. Push one named branch: git push origin <branch>."
    fi
    if is_main_ref "$dst"; then
      deny_pretooluse "Pushing to remote main ('${args[$i]}') is blocked at every phase. main changes only through a PR that the user merges on GitHub. Push the feature branch and open a PR with gh pr create --base main."
    fi
  done
  if (( refspecs == 0 )); then
    deny_pretooluse "'git push' without an explicit branch is blocked: git would pick the target from push.default and the upstream, which may be main, and the hook cannot verify it. Use: git push origin <branch> (a named branch other than main)."
  fi
}

# Local main is written only by a fast-forward sync from origin (`git pull
# --ff-only origin main`, `git fetch origin main:main`). Every explicit
# spelling that rewrites it to something else is denied. Flags are compared
# case-sensitively on purpose: -b/-c create a branch and fail if it exists,
# -B/-C replace it.
check_local_main_rewrite() {
  local seg="$1" tok src dst force flag_hit=0 main_hit=0

  if git_args_into "$seg" fetch || git_args_into "$seg" pull; then
    for tok in "${ARGS[@]+"${ARGS[@]}"}"; do
      [[ "$tok" == -* || "$tok" != *:* ]] && continue
      force=0; [[ "$tok" == +* ]] && force=1
      tok=${tok#+}
      src=${tok%:*}; dst=${tok##*:}
      if is_main_ref "$dst" && { (( force )) || ! is_main_ref "$src"; }; then
        deny_pretooluse "Refspec '${tok}' would rewrite local main from '${src}'. Local main only fast-forwards from origin main; anything else reaches main through a PR."
      fi
    done
  fi

  if git_args_into "$seg" branch; then
    for tok in "${ARGS[@]+"${ARGS[@]}"}"; do
      case "$tok" in
        -f|--force|-M|-m|--move|-C|-d|-D|--delete|-[a-zA-Z]*[fMmCdD]*) flag_hit=1 ;;
        -*) ;;
        *) is_main_ref "$tok" && main_hit=1 ;;
      esac
    done
    if (( flag_hit && main_hit )); then
      deny_pretooluse "Forcing, renaming, overwriting or deleting the local main branch is blocked. Local main only fast-forwards from origin main."
    fi
  fi

  if git_args_into "$seg" update-ref; then
    for tok in "${ARGS[@]+"${ARGS[@]}"}"; do
      if is_main_ref "$tok"; then
        deny_pretooluse "'git update-ref' on main is blocked. Local main only fast-forwards from origin main."
      fi
    done
  fi

  flag_hit=0; main_hit=0
  if git_args_into "$seg" checkout || git_args_into "$seg" switch; then
    for tok in "${ARGS[@]+"${ARGS[@]}"}"; do
      case "$tok" in
        -B|-C|--force-create) flag_hit=1 ;;
        -*) ;;
        *) is_main_ref "$tok" && main_hit=1 ;;
      esac
    done
    if (( flag_hit && main_hit )); then
      deny_pretooluse "Recreating local main at another commit (checkout -B / switch -C main) is blocked. Local main only fast-forwards from origin main."
    fi
  fi
}

# Every path this segment would create, overwrite or delete. Empty when the
# segment only reads.
#
# The parts read DIFFERENT texts on purpose:
#   redirects    <- the RAW segment, walked with quote state, so a '>' in data is
#                   not a redirect and a quoted target is still seen.
#   interpreter  <- the RAW segment with quote CHARACTERS removed, because in
#     payload       `bash -c "echo x > f"` the quoted span is code, not data.
#   mutation     <- the PREPARED scan, whose quoted spans are already stripped
#     verb args     for inert readers, so `grep -n "cp x" f` is not a copy.
write_targets() {
  local raw="$1" scan="$2"
  redirect_targets "$raw"
  if match "$scan" "$INLINE_INTERPRETER_REGEX"; then
    redirect_targets "$(printf '%s' "$raw" | tr -d "\"'")"
  fi
  if match "$scan" "$MUTATION_VERB_REGEX" || match "$scan" "$INPLACE_EDIT_REGEX"; then
    command_targets "$scan" | grep -vE '^\+'
  fi
}

# --- rules -------------------------------------------------------------------

# The leading boundary must be "not part of a word", NOT "whitespace": a quote or
# a paren directly precedes the command in `bash -c 'rm -rf .'` and in
# `git commit -m $(rm -rf .)`. `/` stays outside the class so that an absolute
# invocation like /usr/bin/rm is still recognised. CMD_START comes from
# lib/guard-common.sh, which is also where the write-verb regexes live.
DELETE_VERB_REGEX="${CMD_START}(rm|del|erase|unlink|shred|rmdir|rd|remove-item|ri|clear-content)([[:space:]]|$)"
RECURSIVE_POSIX_REGEX="${CMD_START}rm[[:space:]]+-[a-z]*r[a-z]*([[:space:]]|$)"
RECURSIVE_PS_REGEX="${CMD_START}(remove-item|ri|rm)[[:space:]].*(-recurse|-force)"
RECURSIVE_CMD_REGEX="${CMD_START}(rd|rmdir|del|erase)[[:space:]].*(/s|/q|/f)([[:space:]]|$)"

check_segment() {
  local scan="$1" raw="$2" targets target t

  if match "$scan" '(mkfs|[[:space:]]dd[[:space:]]+if=|:\(\)\{|chmod[[:space:]]+-R[[:space:]]+777[[:space:]]+/)'; then
    deny_pretooluse "Destructive command pattern detected (filesystem/disk level)."
  fi

  if match "$scan" '(format-volume|clear-disk|remove-partition|diskpart|initialize-disk)'; then
    deny_pretooluse "Disk-level destructive PowerShell cmdlet blocked."
  fi

  if match "$scan" "$DELETE_VERB_REGEX" && match "$scan" "$PROTECTED_DELETE_REGEX"; then
    deny_pretooluse "Deletion targeting a protected asset (.env, deployment.json, cloudflared credentials, .git, *.db, payment_notes_archive). These are operator-owned and in part unrecoverable - delete them yourself if you really mean to."
  fi

  if match "$scan" "$RECURSIVE_POSIX_REGEX" || match "$scan" "$RECURSIVE_PS_REGEX" || match "$scan" "$RECURSIVE_CMD_REGEX"; then
    targets=$(extract_targets "$scan")
    if [[ -z "$targets" ]]; then
      deny_pretooluse "Recursive/forced delete with no identifiable target. Blocked because the guard cannot verify what would be removed."
    fi
    while IFS= read -r target; do
      [[ -z "$target" ]] && continue
      if ! is_ephemeral_target "$target"; then
        deny_pretooluse "Recursive/forced delete of '${target}' blocked. Only regenerable build output (build/, bin/, obj/, out/, dist/) and the scratchpad may be removed recursively. Run it yourself if it is genuinely intended."
      fi
    done <<< "$targets"
  fi

  # Always-on write-target rules: user-owned root CLAUDE.md, machine-owned phase
  # state, and the enforcement layer itself. All hold at every phase, in and out
  # of task mode. Targets are normalized and matched case-insensitively: probing
  # showed Server/../CLAUDE.md and .claude/state/task_mode both slipped through.
  while IFS= read -r target; do
    [[ -z "$target" ]] && continue
    t=$(clean_path "$target")
    if is_root_claude_md "$t"; then
      deny_pretooluse "The root CLAUDE.md is user-owned and cannot be modified by Claude under any phase. Folder-level CLAUDE.md files remain editable."
    fi
    if printf '%s' "$t" | grep -qiE "$AGENT_LOCKED_STATE_REGEX"; then
      deny_pretooluse "Phase state is machine-owned: current_phase, task_class, TASK_MODE, the research-block marks (research_files.done, research_web.done) and the sub-agent ledger (agents/) cannot be written from a shell. Use 'bash .claude/hooks/advance.sh' (phase 1 requires the exact task class, phase 2 the finished half) or 'bash .claude/hooks/rollback.sh' from phase 6. Only the user edits these files directly, outside Claude Code."
    fi
    if printf '%s' "$t" | grep -qiE "$HOOK_INFRA_REGEX" && ! hook_edit_grant_active "$STATE_DIR"; then
      # The passphrase is deliberately not quoted here - it grants on a substring
      # match, so echoing it into an error message would make pasting that error
      # back into chat create the grant.
      deny_pretooluse "'${t}' is part of the enforcement layer (settings.json or .claude/hooks/**) and there is no active consent for this turn. Ask the user, in chat, exactly: \"${HOOK_EDIT_GRANT_QUESTION}\" and wait for them to reply with the consent passphrase (recorded in .claude/hooks/README.md; do not paste it from this message). Consent lasts until this turn ends, then must be asked for again. Claude cannot create the grant itself - .claude/state/hook_edit_grant is machine-owned."
    fi
  done < <(write_targets "$raw" "$scan")

  if match "$scan" 'git[[:space:]]+reset[[:space:]]+.*--hard'; then
    deny_pretooluse "'git reset --hard' discards uncommitted work irreversibly. Run it yourself if that is what you want."
  fi

  if match "$scan" 'git[[:space:]]+clean[[:space:]]+-[a-z]*f'; then
    deny_pretooluse "'git clean -f' deletes untracked files - in this repo that includes gitignored runtime state (tokens.json, communicates/, server_logs/, payment_notes_archive/). Run it yourself if intended."
  fi

  if match "$scan" 'git[[:space:]]+branch[[:space:]]+(-D|--delete[[:space:]]+--force|--force[[:space:]]+--delete)'; then
    deny_pretooluse "Force-deleting a branch is blocked."
  fi

  if match "$scan" 'git[[:space:]]+(checkout|restore)[[:space:]]+(--[[:space:]]+)?\.([[:space:]]|$)'; then
    deny_pretooluse "Discarding all worktree changes is blocked. Restore specific files instead."
  fi

  if match "$scan" 'git[[:space:]]+stash[[:space:]]+(drop|clear)'; then
    deny_pretooluse "'git stash drop/clear' destroys stashed work irreversibly."
  fi

  if match "$scan" 'git[[:space:]]+push[[:space:]]+.*(--delete|[[:space:]]:[^[:space:]]+)'; then
    deny_pretooluse "Remote branch deletion is blocked."
  fi

  if match "$scan" 'git[[:space:]]+rm[[:space:]]+' && match "$scan" "$PROTECTED_DELETE_REGEX"; then
    deny_pretooluse "'git rm' targeting a protected asset is blocked."
  fi

  if match "$scan" 'git[[:space:]]+rm[[:space:]]+-[a-z]*r'; then
    deny_pretooluse "'git rm -r' is blocked; remove specific files instead."
  fi

  if match "$scan" ">>?[[:space:]]*[^[:space:]|&;]*${PROTECTED_DELETE_REGEX}"; then
    deny_pretooluse "Redirecting output onto a protected asset would overwrite it."
  fi

  if match "$scan" 'tee[[:space:]]+(-a[[:space:]]+)?[^[:space:]]*'"${PROTECTED_DELETE_REGEX}"; then
    deny_pretooluse "Writing to a protected asset via tee is blocked."
  fi

  if match "$scan" 'git[[:space:]]+pull[[:space:]]+(--rebase|.*[[:space:]]--rebase)'; then
    deny_pretooluse "CLAUDE.md rule: never use 'git pull --rebase'"
  fi

  if match "$scan" 'git[[:space:]]+merge([[:space:]]+--?[[:alnum:]-]+)*[[:space:]]+.*\bmain\b'; then
    deny_pretooluse "git merge involving main is permanently rejected. Use a PR on GitHub for any change to main."
  fi

  if match "$scan" 'git[[:space:]]+push[[:space:]].*(--force([[:space:]]|$)|--force-with-lease|-f([[:space:]]|$))'; then
    deny_pretooluse "Force push blocked"
  fi

  check_push_target "$scan"
  check_local_main_rewrite "$scan"
}

# Read-only phases: no file mutation from the shell, .claude/ and the scratchpad
# excepted (the machine-owned state files are already denied above).
check_segment_readonly() {
  local raw="$1" phase="$2" scan target
  scan=$(prepare_segment "$raw")

  if match "$scan" "$GIT_WRITE_REGEX"; then
    deny_pretooluse "Phase gate: current phase is ${phase} ($(phase_name "$phase")) - read only. Git commands that change the repository or the worktree are blocked; read-only git (log, diff, status, show, blame, ls-files) stays available. Advance with: bash .claude/hooks/advance.sh"
  fi

  while IFS= read -r target; do
    [[ -z "$target" ]] && continue
    if ! is_agent_writable_target "$target"; then
      deny_pretooluse "Phase gate: current phase is ${phase} ($(phase_name "$phase")) - read only. Writing '${target}' from the shell is blocked; only .claude/ and the scratchpad may be written. Complete this phase's work in chat, then run: bash .claude/hooks/advance.sh"
    fi
  done < <(write_targets "$raw" "$scan")
}

SEGMENTS=$(printf '%s' "$CMD" | sed -E 's/(\|\||&&|;|\|)/\n/g')
while IFS= read -r SEG; do
  [[ -z "${SEG//[[:space:]]/}" ]] && continue
  check_segment "$(prepare_segment "$SEG")" "$SEG"
done <<< "$SEGMENTS"

# --- the phase-command whitelist ---------------------------------------------
# Evaluated PER SEGMENT, with all-segments-must-pass semantics.
#
# This used to be a substring match against the whole raw command, and probing
# confirmed the consequences: at phase 8 (shell blocked) `dotnet build && bash
# .claude/hooks/advance.sh` was ALLOWED, and in a read-only phase `echo x >
# Server/pwn.cs && bash .claude/hooks/advance.sh` was ALLOWED - the whitelist
# skipped the checks for the entire line. Mentioning the path in a comment or as
# echoed data worked just as well.
#
# A segment now qualifies only if it IS the invocation, or a bare `cd`, which
# writes nothing. Shell metacharacters are excluded from the argument tail; they
# cannot appear anyway, since the split already happened on them.
PHASE_CMD_RE='^(bash[[:space:]]+)?(\./)?\.claude/hooks/(advance|rollback)\.sh([[:space:]]+[^;&|<>`$]*)?$'
PLAIN_CD_RE='^cd[[:space:]]+[^;&|<>`$]*$'

trim() {
  local s="$1"
  s="${s#"${s%%[![:space:]]*}"}"
  s="${s%"${s##*[![:space:]]}"}"
  printf '%s' "$s"
}

command_is_only_phase_commands() {
  local seg saw=0 t
  while IFS= read -r seg; do
    [[ -z "${seg//[[:space:]]/}" ]] && continue
    t=$(trim "$seg")
    if [[ "$t" =~ $PHASE_CMD_RE ]]; then saw=1; continue; fi
    if [[ "$t" =~ $PLAIN_CD_RE ]]; then continue; fi
    return 1
  done <<< "$SEGMENTS"
  [[ "$saw" == "1" ]]
}

# --- phase gates -------------------------------------------------------------
if [[ -f "${STATE_DIR}/TASK_MODE" ]]; then
  PHASE=$(read_phase "$STATE_DIR")

  if command_is_only_phase_commands; then
    exit 0
  fi

  if [[ "$PHASE" == "8" ]]; then
    if printf '%s' "$CMD" | grep -qE '(^|[[:space:]])gh[[:space:]]+pr[[:space:]]+create([[:space:]]|$)' \
       && printf '%s' "$CMD" | grep -qE -- '--base([[:space:]]+|=)"?main"?([[:space:]"]|$)'; then
      exit 0
    fi
    deny_pretooluse "Phase gate: current phase is 8 ($(phase_name 8)). Shell execution is blocked. Allowed: bash .claude/hooks/advance.sh (to finish the task), gh pr create --base main ... (open the PR for the task)."
  fi

  if phase_is_readonly "$PHASE"; then
    while IFS= read -r SEG; do
      [[ -z "${SEG//[[:space:]]/}" ]] && continue
      check_segment_readonly "$SEG" "$PHASE"
    done <<< "$SEGMENTS"
  fi
fi

exit 0
