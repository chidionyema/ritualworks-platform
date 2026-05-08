You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=C2-sync-reservation-endpoints.md
  BRIEF_ID=C2
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

  # Precondition: catalog StockReservation entity must exist.
  if ! grep -rq "StockReservation" src/Catalog/Catalog.Domain/ 2>/dev/null; then
      echo "BLOCKER: StockReservation aggregate not found in Catalog.Domain — required for this brief. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/checkout/README.md                          (protocol)
  2. docs/agent-briefs/checkout/C2-sync-reservation-endpoints.md   (your specific task)

Then read every file in the brief's "Inputs" section, in order listed.
Do not grep blindly. Do not skim.

================================================================
STEP 3 — EXECUTE
================================================================
  • Touch only files in the brief's "Deliverable" list.
  • If a file already exists with the correct content, SKIP.
  • New commands live under src/Catalog/Catalog.Application/Commands/Reservations/
    so they don't collide with the existing ReserveStockCommand.cs.
  • DO NOT modify src/Catalog/Catalog.Infrastructure/DependencyInjection.cs —
    that's C3's territory. MediatR auto-registers your handlers.
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(catalog): C2 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/checkout-fills/C2
  ✗ Editing src/Catalog/Catalog.Infrastructure/DependencyInjection.cs
  ✗ Editing the existing ReserveStockCommand.cs or any pre-existing command
  ✗ Editing the StockReservation aggregate or domain interfaces
  ✗ Editing files in src/Payments/, src/Orders/, or any other service
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
At the end, paste either the done-report or blocker per
docs/agent-briefs/checkout/README.md. No prose outside those formats.

BEGIN.
