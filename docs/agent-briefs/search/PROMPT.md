# Universal agent prompt — search service briefs

Copy this whole prompt to any coding agent (Gemini CLI, Claude Code, Cursor, Aider, etc.).
Replace **one token** — the brief filename — and paste.

The prompt is **idempotent** (safe to re-run after a crash or partial completion) and **safe** (no push, no deploy, no destructive ops, work isolated in a git worktree so parallel agents can't collide).

---

## The prompt

```
You are a coding agent. Read carefully — these instructions are the contract.

================================================================
THE ONE VARIABLE — REPLACE THIS TOKEN BEFORE SENDING
================================================================
BRIEF_FILE=<REPLACE_ME — e.g. B1-scaffold.md>

================================================================
STEP 1 — SETUP (idempotent, safe to re-run, parallel-safe via git worktree)
================================================================
Run these shell commands in order. If any fail, STOP and report.

  set -euo pipefail
  REPO_ROOT=/Users/chidionyema/Documents/code/ritualworks-platform
  cd "$REPO_ROOT"

  # Derive a stable short id (B1, B2, …) from the brief filename
  BRIEF_ID=$(echo "$BRIEF_FILE" | grep -oE '^B[0-9]+')
  [ -n "$BRIEF_ID" ] || { echo "ERROR: could not parse BRIEF_ID from $BRIEF_FILE" >&2; exit 1; }

  WORKTREE="$REPO_ROOT/../rw-$BRIEF_ID"
  BRANCH="feat/search/$BRIEF_ID"

  git fetch origin --prune

  # Create or reuse a worktree on a dedicated branch off feat/search-service-spec.
  # Re-running the prompt reuses the same worktree — your prior progress is kept.
  if [ ! -d "$WORKTREE" ]; then
      git worktree add "$WORKTREE" -b "$BRANCH" origin/feat/search-service-spec
  fi
  cd "$WORKTREE"

  # Paranoia: verify we're on the expected branch
  CURRENT=$(git rev-parse --abbrev-ref HEAD)
  [ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }

  echo "Worktree ready: $WORKTREE on $BRANCH"

================================================================
STEP 2 — READ (in this order, in full, BEFORE WRITING ANYTHING)
================================================================
  1. docs/agent-briefs/search/README.md      ← protocol, anti-spiral rules,
                                                done-report and blocker formats
  2. docs/agent-briefs/search/$BRIEF_FILE    ← your specific task

Then read every file in the brief's "Inputs" section, in the order listed.
Do not grep blindly. Do not skim.

================================================================
STEP 3 — EXECUTE
================================================================
Follow the brief literally:

  • Touch only files in the brief's "Deliverable" list. No refactors.
    No drive-by edits. No "while I'm here" cleanups.
  • If a file from the Deliverable already exists with the correct
    content (you're re-running after a crash), SKIP it. Don't rewrite.
  • Run every command in "Acceptance". ALL must pass before you finish.
  • Commit locally with a clear message:
        git add <files-from-deliverable>
        git commit -m "feat(search): $BRIEF_ID — <one-line summary>"

================================================================
STEP 4 — HARD STOPS (forbidden — the user does these manually)
================================================================
  ✗ git push  (any branch, any time, including your worktree branch)
  ✗ git push --force
  ✗ git commit --amend   ✗ git rebase   ✗ git reset --hard
  ✗ git commit --no-verify / --no-gpg-sign   ✗ Skipping pre-commit hooks
  ✗ Modifying main, origin, or any branch other than feat/search/$BRIEF_ID
  ✗ Editing files outside the brief's Deliverable list
  ✗ Editing files in other briefs' territory (each brief documents this)
  ✗ Editing CI workflow files unless the brief explicitly authorizes it
  ✗ flyctl deploy   ✗ flyctl secrets set / import   ✗ flyctl scale
  ✗ Opening PRs   ✗ Auto-merging
  ✗ Continuing past 30 minutes — emit a blocker and stop

Allowed (read-only or scoped writes only):
  ✓ Local file create/edit per the brief
  ✓ Running tests, builds, formatters, linters
  ✓ flyctl config validate (read-only)
  ✓ Local git add / git commit (no amend, no force)

================================================================
STEP 5 — OUTPUT (the only thing the reviewer reads)
================================================================
At the end, paste:
  • Either the done-report (success) using the exact format in
    docs/agent-briefs/search/README.md.
  • Or the blocker report (if stuck) using the exact format in the same file.

No prose outside those formats. The reviewer reads dozens of these.

BEGIN.
```

---

## Per-brief variable values

Phase 1 (sequential — run alone):
| `BRIEF_FILE=B1-scaffold.md` |

Phase 2 (run three agents in parallel — separate worktrees, no collision):
| `BRIEF_FILE=B2-meili-schema.md` |
| `BRIEF_FILE=B3-catalog-category-events.md` |
| `BRIEF_FILE=B4-catalog-http-client.md` |

Phase 3 (two parallel):
| `BRIEF_FILE=B5-indexer.md` |
| `BRIEF_FILE=B6-search-endpoint.md` |

Phase 4 (sequential):
| `BRIEF_FILE=B7-bff-and-smoke.md` |

---

## Reviewer workflow (after each done-report)

```bash
# Verify the agent's claims on the actual tree
cd ../rw-B<N>
git log --oneline feat/search-service-spec..HEAD     # what was committed
git diff feat/search-service-spec..HEAD --stat       # what changed
# Re-run the brief's Acceptance commands yourself
```

If green, merge the agent's branch back into `feat/search-service-spec`:

```bash
cd /Users/chidionyema/Documents/code/ritualworks-platform
git checkout feat/search-service-spec
git merge --no-ff feat/search/B<N>
git worktree remove ../rw-B<N>     # cleans up
```

Phase 2 merge order (B2 and B4 both touch DI + csproj — small additive overlaps):
**B3 first → B2 → B4**. Resolve any conflicts during the B4 merge.
