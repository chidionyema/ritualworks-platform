You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=C3-reservation-sweeper.md
  BRIEF_ID=C3
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

  # Precondition: catalog StockReservation entity + Expire() must exist.
  if ! grep -rq "StockReservation" src/Catalog/Catalog.Domain/ 2>/dev/null; then
      echo "BLOCKER: StockReservation aggregate not found in Catalog.Domain. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/checkout/README.md                  (protocol)
  2. docs/agent-briefs/checkout/C3-reservation-sweeper.md  (your specific task)

Then read every file in the brief's "Inputs" section, in order listed.
Do not grep blindly. Do not skim.

================================================================
STEP 3 — EXECUTE
================================================================
  • Touch only files in the brief's "Deliverable" list.
  • If a file already exists with the correct content, SKIP.
  • The ONE existing-file modification is
    src/Catalog/Catalog.Infrastructure/DependencyInjection.cs — add the
    AddOptions / AddSingleton / AddHostedService block exactly as the brief
    specifies, and nothing else.
  • DO NOT modify any controller or API file (C2's territory).
  • Hosted service must register ONLY when !env.IsEnvironment("Test").
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(catalog): C3 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/checkout-fills/C3
  ✗ Editing files in src/Catalog/Catalog.Api/ (controllers — C2's territory)
  ✗ Editing the StockReservation aggregate or any domain interface
  ✗ Editing files in src/Payments/, src/Orders/, or any other service
  ✗ Implementing a real OpenTelemetry-backed IReservationMetrics — Null impl only
  ✗ Adding a sweep API endpoint
  ✗ Editing CI workflow files
  ✗ flyctl deploy / flyctl secrets / flyctl scale
  ✗ Opening PRs / auto-merging
  ✗ Continuing past 30 minutes — emit a blocker and stop

Allowed:
  ✓ Local file create/edit per the brief
  ✓ Running tests, builds, formatters, linters
  ✓ Local git add / git commit (no amend, no force)

================================================================
STEP 5 — OUTPUT
================================================================
Done-report or blocker per docs/agent-briefs/checkout/README.md.
No prose outside those formats.

BEGIN.
