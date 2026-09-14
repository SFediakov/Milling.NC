#!/usr/bin/env bash
# PreToolUse on Bash|PowerShell:
#   - Enforce CLAUDE.md rule: "per user's PR request creation always create PR
#     to origin main". This hook denies `gh pr create` invocations missing or
#     with non-'main' --base, and denies `gh api .../pulls` calls that set
#     base= to anything other than main.
#   - Layer 1 of the PR-target enforcement model. Layer 2 (GitHub branch
#     protection, server-side) is configured separately.
#   - Closes the PowerShell gap on the PR-creation path: gate-bash.sh matches
#     only "Bash"; this hook matches both shells.
#
# jq-free: uses sed/grep for JSON parsing and printf for JSON output.
set -uo pipefail

INPUT=$(cat)

# Extract tool_input.command (mirrors gate-bash.sh extraction).
CMD=$(printf '%s' "$INPUT" | sed -nE 's/.*"command"[[:space:]]*:[[:space:]]*"(([^"\\]|\\.)*)".*/\1/p')
CMD="${CMD//\\\\/\\}"
CMD="${CMD//\\\"/\"}"
CMD="${CMD//\\n/$'\n'}"

deny() {
  local reason="$1"
  local esc=${reason//\\/\\\\}
  esc=${esc//\"/\\\"}
  echo "BLOCKED: $reason" >&2
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"%s"}}\n' "$esc"
  exit 0
}

# Check 1: `gh pr create` must include explicit --base main (or --base=main).
if printf '%s' "$CMD" | grep -qE '(^|[[:space:]])gh[[:space:]]+pr[[:space:]]+create([[:space:]]|$)'; then
  if ! printf '%s' "$CMD" | grep -qE -- '--base([[:space:]]+|=)"?main"?([[:space:]"]|$)'; then
    deny "gh pr create must include explicit '--base main'. Append --base main (no other base is permitted by project policy)."
  fi
fi

# Check 2: merging or approving a PR is the user's decision, never Claude's.
# CLAUDE.md: "per user's PR request creation always create PR to origin main but
# never approve merge request without explicit user agreement". A hook cannot
# read an agreement out of the chat, so the action itself is refused and handed
# back to the user - creating the PR stays allowed.
if printf '%s' "$CMD" | grep -qE '(^|[[:space:]])gh[[:space:]]+pr[[:space:]]+merge([[:space:]]|$)'; then
  deny "'gh pr merge' is blocked. Merging a PR requires the user's explicit agreement and the user performs it. Claude opens the PR and stops there."
fi

if printf '%s' "$CMD" | grep -qE '(^|[[:space:]])gh[[:space:]]+pr[[:space:]]+review([[:space:]]|$)' \
   && printf '%s' "$CMD" | grep -qE -- '--approve([[:space:]]|$)'; then
  deny "Approving a PR is blocked. Review approval is the user's decision."
fi

if printf '%s' "$CMD" | grep -qE 'gh[[:space:]]+api[[:space:]]+.*/pulls/[0-9]+/merge'; then
  deny "Merging a PR through 'gh api' is blocked for the same reason as 'gh pr merge': the merge decision is the user's."
fi

# Check 3: `gh api .../pulls` must not set base= to anything other than main.
if printf '%s' "$CMD" | grep -qE 'gh[[:space:]]+api[[:space:]]+.*/pulls'; then
  apibase=$(printf '%s' "$CMD" | grep -oE -- '(-f|-F|--field)[[:space:]]+base=[^[:space:]"'\''&|;]+' | head -1 | sed -E 's/.*base=//')
  if [[ -n "$apibase" && "$apibase" != "main" ]]; then
    deny "gh api /pulls base must be 'main', got '$apibase'."
  fi
fi

exit 0
