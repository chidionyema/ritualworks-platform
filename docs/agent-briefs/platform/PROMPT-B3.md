You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=B3-reservation-sweeper.md
  BRIEF_ID=B3
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-PB3"
  BRANCH="feat/platform/B3"
  BASE_BRANCH=main

  git fetch origin --prune

  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" "origin/$BASE_BRANCH"
  fi
  cd "$WORKTREE"

  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }

  # Precondition: B1 must be merged into main first.
  if [ ! -f "src/Catalog/Catalog.Domain/StockReservation.cs" ]; then
      echo "BLOCKER: B1 not merged — StockReservation aggregate not on main. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/platform/README.md                (protocol)
  2. docs/agent-briefs/platform/B3-reservation-sweeper.md (your task)

Then read every file in the brief's "Inputs" section, in order listed.

================================================================
STEP 3 — EXECUTE
================================================================
  • Add the BackgroundService + DI + tests.
  • Aggregate Expire() before stock release — load-bearing ordering.
  • Hosted service registered ONLY when !env.IsEnvironment("Test").
  • DO NOT touch Catalog.Api/ (B2's territory).
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(catalog): B3 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/platform/B3
  ✗ Modifying src/Catalog/Catalog.Api/ (B2 territory)
  ✗ Modifying src/Catalog/Catalog.Domain/ (B1 territory)
  ✗ Adding a real OpenTelemetry-backed IReservationMetrics (Null impl only)
  ✗ Adding a sweep API endpoint
  ✗ Modifying any other service
  ✗ Modifying CI workflow files
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
Done-report or blocker per docs/agent-briefs/platform/README.md.
No prose outside those formats.

BEGIN.
