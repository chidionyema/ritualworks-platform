You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=B2-sync-reservation-endpoints.md
  BRIEF_ID=B2
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-PB2"
  BRANCH="feat/platform/B2"
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
  1. docs/agent-briefs/platform/README.md                       (protocol)
  2. docs/agent-briefs/platform/B2-sync-reservation-endpoints.md (your task)

Then read every file in the brief's "Inputs" section, in order listed.

================================================================
STEP 3 — EXECUTE
================================================================
  • Add the two HTTP endpoints in catalog-svc + BFF passthrough.
  • Use HttpContext.GetForwardedUserId() (A1 helper) for user id.
  • New commands live under Catalog.Application/Commands/Reservations/.
  • DO NOT modify Catalog.Infrastructure/DependencyInjection.cs (B3 owns it).
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(catalog): B2 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/platform/B2
  ✗ Modifying src/Catalog/Catalog.Infrastructure/DependencyInjection.cs (B3)
  ✗ Modifying src/Catalog/Catalog.Domain/ (B1 owns)
  ✗ Adding read endpoints (only create + confirm)
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
