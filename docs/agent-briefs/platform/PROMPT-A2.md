You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=A2-per-service-jwt-wiring.md
  BRIEF_ID=A2
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-PA2"
  BRANCH="feat/platform/A2"
  BASE_BRANCH=main

  git fetch origin --prune

  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" "origin/$BASE_BRANCH"
  fi
  cd "$WORKTREE"

  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }

  # Precondition: A1's helpers must already be merged into main.
  if [ ! -f "src/BuildingBlocks/Extensions/AuthenticationExtensions.cs" ]; then
      echo "BLOCKER: A1 helpers missing — AddPlatformAuthentication not on main. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/platform/README.md                  (protocol)
  2. docs/agent-briefs/platform/A2-per-service-jwt-wiring.md (your task)

Then read every file in the brief's "Inputs" section, in order listed.

================================================================
STEP 3 — EXECUTE
================================================================
  • Touch only files in the brief's "Deliverable" list.
  • Five backend services × small edit each (Payments, Catalog, Orders, Search, CheckoutOrchestrator).
  • Do NOT touch identity-svc Program.cs — it's the issuer, already wired.
  • Do NOT touch BFF (A3 territory).
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(platform): A2 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/platform/A2
  ✗ Modifying src/BuildingBlocks/Extensions/AuthenticationExtensions.cs (A1 owns)
  ✗ Modifying src/BffWeb/ (A3 territory)
  ✗ Modifying src/Identity/Identity.Api/Program.cs (already wired as issuer)
  ✗ Modifying any controller (A4 territory)
  ✗ Modifying CI workflow files
  ✗ flyctl deploy / flyctl secrets / flyctl scale
  ✗ Opening PRs / auto-merging
  ✗ Continuing past 30 minutes — emit a blocker and stop

Allowed:
  ✓ Local file create/edit per the brief
  ✓ Modifying deploy/fly/bootstrap.sh ONLY to stage Jwt:* secrets on backend
    services (per the brief's "Per-service Fly toml" section). Nothing else.
  ✓ Running tests, builds, formatters, linters
  ✓ Local git add / git commit (no amend, no force)

================================================================
STEP 5 — OUTPUT
================================================================
Done-report or blocker per docs/agent-briefs/platform/README.md.
No prose outside those formats.

BEGIN.
