You are a coding agent. Read carefully — these instructions are the contract.

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  BRIEF_FILE=B1-scaffold.md
  BRIEF_ID=B1
  cd "$REPO_ROOT"

  WORKTREE="$REPO_ROOT/../rw-$BRIEF_ID"
  BRANCH="feat/search/$BRIEF_ID"

  git fetch origin --prune

  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" origin/feat/search-service-spec
  fi
  cd "$WORKTREE"

  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }
  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/search/README.md      (protocol, anti-spiral rules,
                                               done-report and blocker formats)
  2. docs/agent-briefs/search/B1-scaffold.md (your specific task)

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
        git commit -m "feat(search): B1 — <one-line summary>"

================================================================
STEP 4 — HARD STOPS (forbidden — the user does these manually)
================================================================
  ✗ git push (any branch, any time)
  ✗ git push --force
  ✗ git commit --amend / --no-verify / --no-gpg-sign
  ✗ git rebase / git reset --hard
  ✗ Modifying main, origin, or any branch other than feat/search/B1
  ✗ Editing files outside the brief's Deliverable list
  ✗ Editing other briefs' files
  ✗ Editing CI workflow files unless the brief authorizes it
  ✗ flyctl deploy / flyctl secrets set / flyctl scale
  ✗ Opening PRs / auto-merging
  ✗ Continuing past 30 minutes — emit a blocker and stop

Allowed:
  ✓ Local file create/edit per the brief
  ✓ Running tests, builds, formatters, linters
  ✓ flyctl config validate (read-only)
  ✓ Local git add / git commit (no amend, no force)

================================================================
STEP 5 — OUTPUT
================================================================
At the end, paste either:
  • The done-report (success), exact format from
    docs/agent-briefs/search/README.md.
  • The blocker report (stuck), exact format from the same file.

No prose outside those formats.

BEGIN.
