You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=B1-stock-reservation-refactor.md
  BRIEF_ID=B1
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-PB1"
  BRANCH="feat/platform/B1"
  BASE_BRANCH=main

  git fetch origin --prune

  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" "origin/$BASE_BRANCH"
  fi
  cd "$WORKTREE"

  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }

  # Precondition: Phase A must be merged into main first (B1 doesn't strictly need
  # auth helpers, but Phase B's HTTP endpoints in B2 do — we want a clean ordering).
  if [ ! -f "src/BuildingBlocks/Extensions/AuthenticationExtensions.cs" ]; then
      echo "BLOCKER: Phase A not merged into main yet. Phase B should not start. STOP." >&2
      exit 1
  fi
  if [ ! -f "src/Catalog/Catalog.Domain/OrderStockReservation.cs" ]; then
      echo "BLOCKER: OrderStockReservation aggregate not found — refactor target missing. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/platform/README.md                       (protocol)
  2. docs/agent-briefs/platform/B1-stock-reservation-refactor.md (your task)

Then read every file in the brief's "Inputs" section, in order listed.
Crucially: grep for every callsite of OrderStockReservation in src/Catalog
before writing — the saga path's CreateConfirmed shortcut must not break it.

================================================================
STEP 3 — EXECUTE
================================================================
  • Refactor the domain aggregate; rename file + class.
  • Add migration AddStockReservationLifecycle (EF migration command in the brief).
  • Update saga callsites to use the new CreateConfirmed factory.
  • Existing Catalog.Integration tests must pass — that's the load-bearing gate.
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(catalog): B1 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/platform/B1
  ✗ Adding HTTP endpoints (B2 territory)
  ✗ Adding the sweeper background service (B3 territory)
  ✗ Modifying any other service (src/Payments/, src/Orders/, etc.)
  ✗ Altering existing saga events (StockReservationRequestedEvent etc.)
  ✗ Modifying CI workflow files
  ✗ flyctl deploy / flyctl secrets / flyctl scale
  ✗ Opening PRs / auto-merging
  ✗ Continuing past 30 minutes — emit a blocker and stop

Allowed:
  ✓ Local file create/edit per the brief
  ✓ Running tests, builds, formatters, linters
  ✓ EF migration commands (dotnet ef migrations add … — read-only on the DB)
  ✓ Local git add / git commit (no amend, no force)

================================================================
STEP 5 — OUTPUT
================================================================
Done-report or blocker per docs/agent-briefs/platform/README.md.
No prose outside those formats.

BEGIN.
