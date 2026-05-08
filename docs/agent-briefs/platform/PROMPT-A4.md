You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=A4-migrate-existing-controllers.md
  BRIEF_ID=A4
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-PA4"
  BRANCH="feat/platform/A4"
  BASE_BRANCH=main

  git fetch origin --prune

  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" "origin/$BASE_BRANCH"
  fi
  cd "$WORKTREE"

  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }

  # Precondition: A1's GetForwardedUserId helper must exist on main.
  if [ ! -f "src/BuildingBlocks/Extensions/HttpContextExtensions.cs" ]; then
      echo "BLOCKER: A1 helpers missing on main. STOP." >&2
      exit 1
  fi

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/platform/README.md                          (protocol)
  2. docs/agent-briefs/platform/A4-migrate-existing-controllers.md (your task)

Then read every file in the brief's "Inputs" section, in order listed.

================================================================
STEP 3 — EXECUTE
================================================================
  • Touch only controllers + tests for the migration. No new endpoints.
  • Replace User.FindFirstValue(NameIdentifier)/("sub") → HttpContext.GetForwardedUserId().
  • Update test fixtures so authenticated calls send X-User-Id.
  • Add a "header missing → 401" test per migrated controller.
  • Run every command in "Acceptance". ALL must pass.
  • Commit locally:
        git add <files-from-deliverable>
        git commit -m "feat(platform): A4 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS
================================================================
  ✗ git push / git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/platform/A4
  ✗ Modifying src/BuildingBlocks/ (A1 owns)
  ✗ Modifying any service's Program.cs (A2/A3 territory)
  ✗ Adding new endpoints — migration only
  ✗ Inventing new claim types or header names other than X-User-Id
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
