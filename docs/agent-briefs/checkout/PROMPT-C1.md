You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=C1-subscription-endpoints.md
  BRIEF_ID=C1
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-$BRIEF_ID"
  BRANCH="feat/checkout-fills/$BRIEF_ID"
  BASE_BRANCH=main

  git fetch origin --prune

  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" "origin/$BASE_BRANCH"
  fi
  cd "$WORKTREE"

  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }

  # Precondition: payments-svc backend services must already exist.
  if [ ! -f "src/Payments/Payments.Application/Interfaces/ISubscriptionManager.cs" ]; then
      echo "BLOCKER: ISubscriptionManager not found — payments-svc subscription backend appears unmigrated. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/checkout/README.md                     (protocol, anti-spiral
                                                                rules, done-report
                                                                and blocker formats)
  2. docs/agent-briefs/checkout/C1-subscription-endpoints.md  (your specific task)

Then read every file in the brief's "Inputs" section, in order listed.
Do not grep blindly. Do not skim.

================================================================
STEP 3 — EXECUTE
================================================================
  • Touch only files in the brief's "Deliverable" list. No refactors,
    no drive-by edits, no "while I'm here" cleanups.
  • If a file from the Deliverable already exists with the correct
    content (re-running after a crash), SKIP it. Don't rewrite.
  • Run every command in "Acceptance". ALL must pass before you finish.
  • Commit locally with a clear message:
        git add <files-from-deliverable>
        git commit -m "feat(payments): C1 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS (forbidden — the user does these manually)
================================================================
  ✗ git push (any branch, any time)
  ✗ git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/checkout-fills/C1
  ✗ Editing files outside the brief's Deliverable list
  ✗ Editing files belonging to C2, C3, or C4 (each brief documents its turf)
  ✗ Editing CI workflow files
  ✗ flyctl deploy / flyctl secrets set / flyctl scale
  ✗ Opening PRs / auto-merging
  ✗ Continuing past 30 minutes — emit a blocker and stop

Allowed:
  ✓ Local file create/edit per the brief
  ✓ Running tests, builds, formatters, linters
  ✓ Local git add / git commit (no amend, no force)

================================================================
STEP 5 — OUTPUT
================================================================
At the end, paste either:
  • The done-report (success), exact format from
    docs/agent-briefs/checkout/README.md.
  • The blocker report (stuck), exact format from the same file.

No prose outside those formats.

BEGIN.
