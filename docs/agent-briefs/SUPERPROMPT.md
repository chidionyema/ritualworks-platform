# SUPERPROMPT — universal parallel-agent launcher

A single prompt template any LLM CLI (Gemini, Claude, etc.) can run, with any brief in this repo, to do a scoped piece of work in an isolated git worktree without colliding with sibling agents.

## Two modes

**Mode A — Operator-assigned.** Operator hand-picks one brief per agent and fills in the Parameters block. Each agent does exactly one track and reports back.

**Mode B — Autonomous wave.** Operator launches N identical agents with the same prompt against a track list. Each agent atomically claims an unclaimed track, implements it, pushes, queues an auto-merge PR, then loops to claim the next. No coordinator. Race-safe via remote ref push. Walk away.

Pick Mode A when scope is small and operator wants explicit control. Pick Mode B for "5 mutually-independent tracks, fire and forget."

The **HARD RULES** at the bottom apply to both modes.

---

## Mode A — Operator-assigned (per-agent prompt)

### Parameters (operator fills these in before handing to the agent)

```
REPO_ROOT=/Users/chidionyema/Documents/code/haworks-platform
BASE_BRANCH=feat/audit-service                        # the branch the worktree forks from
BRIEF_FILE=docs/agent-briefs/audit/L1A-extractors-redactor.md
TRACK_ID=audit-L1A                                    # short slug, used in worktree path + branch name
TIME_BUDGET_MINUTES=45
WORKTREE_PARENT=/Users/chidionyema/Documents/code     # where rw-<TRACK_ID> will be created
```

If any parameter is unset, **STOP** and ask. Do not invent defaults.

The branch the agent commits on is derived: `BRANCH=${BASE_BRANCH}-${TRACK_ID}`. If `BASE_BRANCH` already ends in `${TRACK_ID}`, use `BASE_BRANCH` as-is.

---

## Contract — what this prompt guarantees

1. **Isolation.** The agent works in its own worktree (`${WORKTREE_PARENT}/rw-${TRACK_ID}`). Sibling agents can run concurrently without filesystem conflicts.
2. **Scope discipline.** The brief's Deliverable list defines what files the agent may create / modify. `git status` after the work runs must list ONLY those files. Anything else = scope violation = blocker.
3. **No silent failures.** Every Acceptance command in the brief must exit 0. If one fails, the agent debugs within its scope; if it can't, it emits a Blocker report and STOPS.
4. **Single commit per track.** One commit per agent run, on the dedicated branch. Iterations within the run use `git commit --amend` until the run is reported complete.
5. **Standard reporting.** The agent ends with either the Done-Report template (success) or the Blocker template (couldn't finish).

---

## Phase 0 — Pre-flight (≤ 30 seconds)

```bash
set -euo pipefail

# Validate every parameter is set and non-empty
for var in REPO_ROOT BASE_BRANCH BRIEF_FILE TRACK_ID TIME_BUDGET_MINUTES WORKTREE_PARENT; do
    [ -n "${!var:-}" ] || { echo "ERROR: $var is unset" >&2; exit 1; }
done

# Repo + brief must exist
[ -d "$REPO_ROOT/.git" ] || [ -f "$REPO_ROOT/.git" ] || { echo "ERROR: $REPO_ROOT is not a git repo" >&2; exit 1; }
[ -f "$REPO_ROOT/$BRIEF_FILE" ] || { echo "ERROR: brief not found at $REPO_ROOT/$BRIEF_FILE" >&2; exit 1; }

# Base branch must resolve
git -C "$REPO_ROOT" rev-parse --verify "$BASE_BRANCH" >/dev/null 2>&1 \
    || git -C "$REPO_ROOT" rev-parse --verify "origin/$BASE_BRANCH" >/dev/null 2>&1 \
    || { echo "ERROR: $BASE_BRANCH does not exist locally or on origin" >&2; exit 1; }

WORKTREE="$WORKTREE_PARENT/rw-$TRACK_ID"
# Derive BRANCH: if BASE_BRANCH already ends in -$TRACK_ID, reuse it; else append
case "$BASE_BRANCH" in
    *-"$TRACK_ID") BRANCH="$BASE_BRANCH" ;;
    *)             BRANCH="${BASE_BRANCH}-${TRACK_ID}" ;;
esac

echo "Pre-flight OK"
echo "  REPO_ROOT       = $REPO_ROOT"
echo "  BASE_BRANCH     = $BASE_BRANCH"
echo "  WORKTREE        = $WORKTREE"
echo "  BRANCH          = $BRANCH"
echo "  BRIEF_FILE      = $BRIEF_FILE"
echo "  TIME_BUDGET     = $TIME_BUDGET_MINUTES minutes"
```

If any of those fail, **STOP** and emit a Blocker report. Do not patch around missing parameters.

---

## Phase 1 — Worktree setup (≤ 2 minutes)

Idempotent — re-runs are safe.

```bash
cd "$REPO_ROOT"
git fetch origin --prune --quiet || true   # best-effort; offline runs still proceed

if [ ! -d "$WORKTREE" ]; then
    # If branch already exists locally, use it; otherwise create it from BASE_BRANCH
    if git rev-parse --verify "$BRANCH" >/dev/null 2>&1; then
        git worktree add "$WORKTREE" "$BRANCH"
    elif git rev-parse --verify "origin/$BRANCH" >/dev/null 2>&1; then
        git worktree add "$WORKTREE" -b "$BRANCH" "origin/$BRANCH"
    else
        # Resolve BASE_BRANCH (local first, then origin)
        if git rev-parse --verify "$BASE_BRANCH" >/dev/null 2>&1; then
            BASE_REF="$BASE_BRANCH"
        else
            BASE_REF="origin/$BASE_BRANCH"
        fi
        git worktree add "$WORKTREE" -b "$BRANCH" "$BASE_REF"
    fi
fi

cd "$WORKTREE"
CURRENT=$(git rev-parse --abbrev-ref HEAD)
[ "$CURRENT" = "$BRANCH" ] || { echo "ERROR: expected $BRANCH, on $CURRENT" >&2; exit 1; }
echo "Worktree ready: $WORKTREE on $BRANCH"

# Snapshot the working tree BEFORE work begins — used in Phase 5 to verify scope
git status --porcelain > /tmp/superprompt-pre-work-status-$TRACK_ID.txt
```

---

## Phase 2 — Read (≤ 15 min, hard cap)

The brief is exactly one file: `$REPO_ROOT/$BRIEF_FILE`. You do NOT read other briefs in `docs/agent-briefs/`, even sibling ones in the same subdirectory.

Read these files in this order, **in full**, **before writing or editing anything**:

1. **`$REPO_ROOT/$BRIEF_FILE`** — the brief itself. This is the authoritative scope statement for your track.
2. **The README.md in the brief's directory if it exists** (e.g., if `$BRIEF_FILE` is `docs/agent-briefs/audit/L1A-extractors-redactor.md`, also read `docs/agent-briefs/audit/README.md`). The README carries the protocol + done-report templates for that brief family. If no README exists in that directory, skip — your brief is self-contained.
3. **Every file the brief lists under its "Inputs" section**, in the order listed.

Do NOT:
- Read other brief files in `docs/agent-briefs/` (even ones that look related). Your brief plus its Inputs is the deliberate context window. Trust it.
- Grep blindly for things the brief doesn't ask for.
- Skim. If you can't recall what an Input file said, re-read it.
- Read files outside the Inputs list "to get more context".

If an Input file the brief claims exists doesn't, **STOP** and emit a Blocker.

---

## Phase 3 — Execute (within the time budget)

Implement the brief's "Deliverable" list, exactly:
- Create the listed new files with the listed contents.
- Modify the listed existing files only as the brief specifies.
- Do NOT touch any file outside the Deliverable list. If you find yourself wanting to, that's a scope violation — emit a Blocker instead.

While working:
- **Time budget.** If you've burned `TIME_BUDGET_MINUTES` and are not approaching green, **STOP** and emit a Blocker. Do not keep retrying the same approach.
- **No drive-by refactors.** If you spot something that "would only take a minute," log it under "out-of-scope observations" in the done-report. Do not change it.
- **No package additions outside the brief.** If a NuGet / npm / etc. package isn't already in the project's dependency list, do not add it unless the brief explicitly says to.
- **No skipping tests.** If a test in the brief's Acceptance fails, fix the code (or the test if it's clearly wrong), don't `[Fact(Skip=…)]` it.

---

## Phase 4 — Verify

Run **every** command in the brief's "Acceptance" section. Capture exit codes.

```bash
# Example shape; substitute the brief's actual commands.
dotnet build src/X/X.csproj -c Release --nologo --verbosity quiet
dotnet test  tests/X.Unit/X.Unit.csproj -c Release --logger "console;verbosity=minimal"
```

Every Acceptance command must exit 0. If one fails:
1. Re-read the failing command's output. The error is usually a missing reference, a typo, or a real test failure.
2. Fix within scope. Don't expand to other phases' files.
3. If you can't fix within scope and within remaining time budget, **STOP** and emit a Blocker.

---

## Phase 5 — Scope verification + Commit

Before committing, verify your changes are inside scope:

```bash
cd "$WORKTREE"

# What's actually changed?
git status --porcelain | sort > /tmp/superprompt-post-work-status-$TRACK_ID.txt
diff /tmp/superprompt-pre-work-status-$TRACK_ID.txt /tmp/superprompt-post-work-status-$TRACK_ID.txt
```

Compare the file list to the brief's Deliverable list. Every changed file MUST be on the brief's Deliverable list. If any unexpected file appears (incl. accidental edits, IDE artifacts, build output not in `.gitignore`), **STOP** and emit a Blocker; do not proceed to commit.

When clean, commit:

```bash
git add <ONLY the files in the brief's Deliverable list>   # NOT git add -A; be explicit
git commit -m "$(cat <<'EOF'
<commit message from the brief's commit block>
EOF
)"
```

The brief specifies the exact commit message. Do not freelance.

If the brief is silent on the commit message, derive: `<type>(<area>/<TRACK_ID>): <one-line summary>` (matching the repo's existing commit-message style).

**One commit per track.** Iterations within the same run use `git commit --amend` (only on the unpushed commit on this branch).

---

## Phase 6 — Report

Emit one of the two templates below, filled in.

### Done-report (success path)

```
## Track <TRACK_ID> — done

### Files created
- <path>
- ...

### Files modified
- <path> (one-line summary of the change)
- ...

### Acceptance
- `<acceptance command 1>`: ✓ / ✗
- `<acceptance command 2>`: ✓ / ✗
(every command from the brief, with the actual outcome)

### Commit
- <short hash> <subject line>

### Out-of-scope observations
(things you noticed that need follow-up, but did NOT touch — empty if none)
- ...

### Blockers
(empty if none, otherwise — but if any, you should have used the Blocker template instead)
```

### Blocker template (any failure path)

```
## Track <TRACK_ID> — BLOCKED

### What I tried
- step 1 (cmd / file edit) → outcome
- step 2 → outcome

### Where I'm stuck
(one paragraph, no waffle)

### What I need
(specific: a missing file, a clarification, a sibling track's interface, …)

### Files left in flight
(uncommitted edits the reviewer will want to see)
- <path>
- ...

### Time spent
<minutes> of <TIME_BUDGET_MINUTES>
```

---

## Mode B — Autonomous wave (one prompt, N agents, self-claim)

You are ONE OF N parallel coding agents. The track list and the brief that describes each track are in the operator-supplied **Parameters** block below. Each agent claims, implements, pushes, queues auto-merge, and loops until no claimable tracks remain.

### Parameters (operator fills these in once; the same filled-in prompt goes to every agent)

```
REPO=/Users/chidionyema/Documents/code/haworks-platform
GH_REPO=chidionyema/haworks-platform
BASE_BRANCH=<<REPLACE_WITH_BASE_BRANCH>>               # e.g. main, feat/audit-service
BRIEF_FILE=<<REPLACE_WITH_BRIEF_PATH>>                 # e.g. docs/agent-briefs/audit/parallel-tracks.md
TRACK_PREFIX=<<REPLACE_WITH_TRACK_PREFIX>>             # e.g. feat/audit-   ← full branch = ${TRACK_PREFIX}<id>
TRACKS=(<<REPLACE_WITH_TRACK_IDS>>)                    # e.g. (L1A L1B L1C L1D) for audit
WORKTREE_PARENT=/tmp                                   # ephemeral worktrees go here
```

> **Operator:** every `<<REPLACE_WITH_*>>` token MUST be substituted with the real value before the prompt is handed to an agent. Pasting this block as-is into a Gemini session will cause Pre-flight to abort.

> **Agent:** if any `<<REPLACE_WITH_*>>` token survives in your filled-in prompt, **STOP** before Step 1 and emit:
> ```
> ## BLOCKED — unfilled parameter placeholder
> The following parameters still contain <<REPLACE_WITH_*>> tokens: <list>
> Operator must substitute real values before re-running.
> ```
> Do NOT invent values. Do NOT proceed.

The brief MUST contain one `### Track <id>` section per id in `TRACKS`, plus a `## Universal rules` / `## Anti-stuck` / `## Reference file` H2 section every track inherits. Tracks are mutually independent (no ordering required between them).

### Pre-flight (Mode B) — run before Step 1

```bash
set -euo pipefail

# 1. No placeholders survived
for v in BASE_BRANCH BRIEF_FILE TRACK_PREFIX; do
    val="${!v}"
    case "$val" in
        *"<<REPLACE_WITH_"*)
            echo "ERROR: $v still contains a <<REPLACE_WITH_*>> placeholder ($val). Operator must fill it in." >&2
            exit 1
            ;;
    esac
done
[ "${#TRACKS[@]}" -gt 0 ] || { echo "ERROR: TRACKS array is empty" >&2; exit 1; }
case "${TRACKS[*]}" in
    *"<<REPLACE_WITH_"*) echo "ERROR: TRACKS contains a placeholder" >&2; exit 1 ;;
esac

# 2. Brief exists and looks well-formed for THIS track family
[ -f "$REPO/$BRIEF_FILE" ] || { echo "ERROR: brief not found at $REPO/$BRIEF_FILE" >&2; exit 1; }

for t in "${TRACKS[@]}"; do
    if ! grep -qE "^### Track $t($|[: ])" "$REPO/$BRIEF_FILE"; then
        echo "ERROR: brief at $BRIEF_FILE has no '### Track $t' section. Wrong brief or missing track." >&2
        exit 1
    fi
done

for h in "## Universal rules" "## Anti-stuck" "## Reference file"; do
    if ! grep -qF "$h" "$REPO/$BRIEF_FILE"; then
        echo "ERROR: brief at $BRIEF_FILE missing required H2: '$h'" >&2
        exit 1
    fi
done

echo "Pre-flight OK — brief at $BRIEF_FILE has all ${#TRACKS[@]} tracks + universal sections"
```

If any check fails: STOP, do not claim anything. Report to operator.

### Step 1 — Claim a track (atomic, no coordinator)

Pre-flight (above) MUST have passed before reaching here.

```bash
set -euo pipefail
git -C "$REPO" fetch origin --prune

CLAIMED=""
for t in "${TRACKS[@]}"; do
    BRANCH="${TRACK_PREFIX}${t}"

    # Already merged into THIS BASE_BRANCH? skip permanently.
    # (--base "$BASE_BRANCH" prevents collisions with merged PRs on other bases —
    # the bug that made agents exit early when reusing prior waves' branch names.)
    if gh pr list -R "$GH_REPO" --state merged --base "$BASE_BRANCH" --head "$BRANCH" --json number --jq '.[].number' | grep -q .; then continue; fi

    # Branch already on origin? Distinguish live claims from zombies.
    # "alive"  = ≥1 commit beyond base (the sentinel below + real work).
    # "zombie" = 0 commits beyond base — agent died before its sentinel push.
    # Race-window: a freshly-claimed branch sits at 0 commits for the few seconds
    # between the atomic-claim push and the sentinel push. Don't false-reap a live
    # agent in that window — re-check after a generous delay before declaring dead.
    # (Also protects legacy/old-script agents that never push a sentinel until their
    # first real commit lands; they get the full ZOMBIE_CONFIRM_SECS to push.)
    if git -C "$REPO" ls-remote --exit-code --heads origin "$BRANCH" >/dev/null 2>&1; then
        git -C "$REPO" fetch origin "$BRANCH:refs/remotes/origin/$BRANCH" 2>/dev/null || true
        ahead=$(git -C "$REPO" rev-list --count "origin/${BASE_BRANCH}..origin/${BRANCH}" 2>/dev/null || echo "?")
        if [ "$ahead" = "0" ]; then
            ZOMBIE_CONFIRM_SECS="${ZOMBIE_CONFIRM_SECS:-90}"
            echo "[?] $BRANCH at 0 commits — could be mid-claim. Confirming dead in ${ZOMBIE_CONFIRM_SECS}s…"
            sleep "$ZOMBIE_CONFIRM_SECS"
            git -C "$REPO" fetch origin "$BRANCH:refs/remotes/origin/$BRANCH" 2>/dev/null || true
            ahead=$(git -C "$REPO" rev-list --count "origin/${BASE_BRANCH}..origin/${BRANCH}" 2>/dev/null || echo "?")
            if [ "$ahead" = "0" ]; then
                echo "[reclaim] $BRANCH still at 0 commits after ${ZOMBIE_CONFIRM_SECS}s — really dead, deleting"
                git -C "$REPO" push origin --delete "$BRANCH" 2>/dev/null || continue
            else
                echo "[skip] $BRANCH advanced during confirm window — alive after all"
                continue
            fi
        else
            continue
        fi
    fi

    # Atomic claim: race-safe push of a branch ref pointing at origin/$BASE_BRANCH.
    # First push wins; concurrent loser falls through to the next track id.
    if git -C "$REPO" push origin "refs/remotes/origin/${BASE_BRANCH}:refs/heads/${BRANCH}" 2>/dev/null; then
        # Refresh local tracking ref so the worktree forks from the right commit
        # and `git status` doesn't report "upstream is gone". Required because the
        # `refs/remotes/origin/$BASE:refs/heads/$BRANCH` push above does NOT update
        # the local `refs/remotes/origin/$BRANCH` ref.
        git -C "$REPO" fetch origin "$BRANCH:refs/remotes/origin/$BRANCH"
        CLAIMED=$t
        break
    fi
done

if [ -z "$CLAIMED" ]; then
    echo "[exit] all tracks claimed or merged"
    exit 0
fi

# Dedicated worktree to avoid the shared-checkout race
WT="${WORKTREE_PARENT}/$(basename "$TRACK_PREFIX")${CLAIMED}"
git -C "$REPO" worktree remove --force "$WT" 2>/dev/null || true
rm -rf "$WT"
# Drop any stale local branch from a prior failed run before re-creating
git -C "$REPO" branch -D "${TRACK_PREFIX}${CLAIMED}" 2>/dev/null || true
# Create worktree on a fresh local branch tracking origin/$BRANCH explicitly
git -C "$REPO" worktree add -b "${TRACK_PREFIX}${CLAIMED}" --track "$WT" "origin/${TRACK_PREFIX}${CLAIMED}"

# Sentinel commit — proves this claim is alive to future waves' zombie detector.
# Without it, if the agent dies before its first real commit, the branch sits at
# 0 commits beyond base and the next wave will (correctly) reclaim it. The sentinel
# embeds host+pid so an operator can see who claimed when from `git log`.
git -C "$WT" commit --allow-empty -m "claim(${CLAIMED}): pid=$$ host=$(hostname) ts=$(date -u +%FT%TZ)"
git -C "$WT" push origin "${TRACK_PREFIX}${CLAIMED}"

echo "[ready] TRACK=$CLAIMED BRANCH=${TRACK_PREFIX}${CLAIMED} WORKTREE=$WT"
```

If two agents try the same `git push` simultaneously, exactly one wins — the other's push fails with `non-fast-forward` and falls through to the next iteration of the loop. The atomic claim is the remote ref push itself.

### Step 2 — Read ONLY your track's section

The brief is one file: `$REPO/$BRIEF_FILE`. Inside it, you read **exactly four** sections, no more, no less:

1. The H2 section titled `## Universal rules` — applies to every track.
2. The H2 section titled `## Anti-stuck` — decision rules for when you're stuck.
3. The H2 section titled `## Reference file` — the canonical existing file you mirror when in doubt.
4. The H3 section whose heading **starts with** `### Track $CLAIMED` (typically `### Track $CLAIMED:` followed by a human-readable name).

If any of those four sections is missing from the brief, **STOP** — the brief is malformed and not safe to execute. Release the claim (`git push origin --delete "${TRACK_PREFIX}${CLAIMED}"`) and exit.

To extract just your scope cleanly:

```bash
# Universal + Anti-stuck + Reference file (everything between `## Universal rules` and the first `### Track`)
awk '/^## Universal rules/,/^### Track /' "$REPO/$BRIEF_FILE" | sed '/^### Track /d' > /tmp/scope-shared-$CLAIMED.md

# Your track section (from `### Track $CLAIMED` to the next `### Track ` or EOF)
awk -v t="### Track $CLAIMED" '
    $0 ~ "^"t"($|[: ])" { p=1 }
    p && /^### Track / && !($0 ~ "^"t"($|[: ])") { exit }
    p { print }
' "$REPO/$BRIEF_FILE" > /tmp/scope-track-$CLAIMED.md
```

Read both files in full. Do **NOT** read other tracks' sections — you don't need them and reading them invites scope creep.

Inside your track section, the brief MUST use these labelled lines (the brief author's contract — refuse to proceed if missing):

- `**Files you own (exclusive):**` — paths you create / edit. Anything else is out of scope.
- `**Files you may NOT touch:**` — explicit forbidden paths (when not obvious).
- `**Reference to mirror:**` — the file to copy patterns from when you're stuck.
- `**NuGet (if any):**` — at most one allowed package addition; `none` if none.
- `**Done:**` — the shell command that returns 0 only when your work is correct.

### Step 3 — Implement, commit, push (per file group)

For each logical file group in your track section (e.g., entities → options → provider → extensions → tests):

1. Create / edit the files in that group.
2. Build verify within scope:
   ```bash
   dotnet build "$WT/src/<your-area>" --nologo --verbosity quiet
   ```
   Must return exit 0 before committing. If it fails, fix forward; do not commit broken builds.
3. Commit + push immediately:
   ```bash
   git -C "$WT" add <explicit list of files in this group>
   git -C "$WT" commit -m "<type>(<area>-${CLAIMED}): <one-line summary>"
   git -C "$WT" push origin "${TRACK_PREFIX}${CLAIMED}"
   ```

**Per-group commits + per-group push.** Not "one big commit at the end." This gives the operator visibility into progress and a recovery point per group.

### Step 4 — Run the track's "Done" check

Every track section ends with a `Done:` shell command. Run it verbatim. It must exit 0.

### Step 5 — Open + auto-merge PR

```bash
git -C "$WT" push origin "${TRACK_PREFIX}${CLAIMED}"

gh pr create -R "$GH_REPO" \
    --base "$BASE_BRANCH" \
    --head "${TRACK_PREFIX}${CLAIMED}" \
    --title "$(git -C "$WT" log -1 --pretty=%s)" \
    --body "Implements track ${CLAIMED} per ${BRIEF_FILE}"

PR=$(gh pr view "${TRACK_PREFIX}${CLAIMED}" -R "$GH_REPO" --json number --jq .number)
gh pr merge "$PR" -R "$GH_REPO" --auto --squash --delete-branch
```

`--auto` queues the merge; GitHub merges as soon as required checks go green. Do **NOT** merge by hand.

### Step 6 — Cleanup + loop

```bash
git -C "$REPO" worktree remove --force "$WT" 2>/dev/null
```

Then go back to **Step 1** and try to claim another track. Loop until claim returns `[exit] all tracks claimed or merged`.

When the loop exits, output exactly one line and STOP:

```
[agent done] tracks: <comma-separated list of tracks this agent claimed>
```

### Operator wave-launch

```bash
# In N (1..N) terminals, paste the SAME filled-in superprompt into each gemini session.
# Each self-claims, implements, auto-merges, loops.
```

### Stuck-agent recovery

If an agent gets stuck mid-track (claims but never finishes), the branch sits on origin uncompleted. Recovery:

```bash
# Drop the orphan claim so a fresh agent can pick it up
git push origin --delete "${TRACK_PREFIX}<id>"
# Optionally: also close the agent's stuck PR if one was opened
gh pr close <pr#> -R "$GH_REPO"
# Then start a fresh agent with the same prompt
```

The atomic-claim protocol prevents two agents from grabbing the same id; deleting the remote branch effectively "releases" the claim for the next agent.

---

## HARD RULES — apply to BOTH modes (violate any and you have failed)

### Process discipline

- **Read your scope first.** Mode A: the brief's Inputs in order. Mode B: only your track's section + universal/anti-stuck. Do NOT read other tracks' sections.
- **Time budget.** Default 45 minutes per track (Mode A) / per claim iteration (Mode B). Hit it and not green? STOP. Mode A → Blocker. Mode B → release the claim (`git push origin --delete <branch>`) and exit; another agent picks it up.
- **60-second decision time-box.** Naming, file location, dependency choice over budget? **Mirror the reference file** named in the brief. Move on. Don't deliberate.
- **If thinking instead of doing, you are stuck.** Mirror the reference file. Move on. Re-reading the brief for the third time is a symptom.

### Scope discipline

- **No cross-track edits.** Need a sibling's code to change? Mode A → Blocker. Mode B → write a `// TODO(<area>-<TRACK>)` comment in YOUR file describing the cross-track change and continue without making it.
- **No silent scope expansion.** Drive-by refactors get logged in out-of-scope observations, never executed.
- **No `csproj` edits except adding the SINGLE NuGet package your track needs**, named in your section. Don't restructure references, don't reorder ItemGroups, don't bump versions.
- **No new public types in shared namespaces.** New types live in your owned subdirectory only.
- **No touching files outside your owned paths.** Mode A: outside Deliverable. Mode B: outside the track section's Files-You-Own list.

### Git discipline

- **Never `cd`.** Working directory does NOT persist across bash tool calls. Every git op uses `git -C $REPO` or `git -C $WT`. Every file path is absolute.
- **`git add <explicit-list>`, never `git add -A` or `git add .`.** The latter pulls in another track's untracked work and breaks the parallel contract.
- **Per-file-group commit + push (Mode B).** Not "one big commit at the end." Entities → commit + push. Validators → commit + push. Handlers → commit + push. Tests → commit + push. The push is the progress signal.
- **Don't drop stashes without inspecting.** `git stash show -p stash@{N}` before `git stash drop`. Recovery from accidental drops: `git fsck --lost-found` finds dangling commits, `git stash apply <hash>` restores.
- **Never `git clean -fdx`, `git reset --hard`, `git push --force` (without `--force-with-lease`), `git checkout --` over uncommitted edits.** They destroy unstaged or untracked work. If state is confusing, STOP and report.
- **Never merge to `main` by hand.** Use `gh pr merge --auto --squash --delete-branch` (Mode B) or hand back to the operator (Mode A).

### Communication discipline (especially for Mode B)

- **No questions to user.** The operator is not in the session. Decision unclear? Mirror the reference file. Still unclear? Pick the simpler option, add `// TODO(<area>-<TRACK>): <reason>`, proceed.
- **No narrating.** After each commit/push, do the next file. After `[agent done]`, stop. No status updates, no "now I will…", no progress prose.
- **Don't fabricate paths.** If a file the Inputs / track section claims exists doesn't, file a Blocker (Mode A) or write the missing file from the brief's spec if specified, otherwise stop (Mode B).
- **Trust but verify the spec.** If the spec says X but the code says Y, the spec is the source of truth — but pause and note it. Don't silently rewrite the spec; don't silently break from it.

### Reporting

- **Mode A:** Done-report or Blocker template (below).
- **Mode B:** Per-track, no per-track report — the audit trail is the per-group commits + the auto-merge PR. At the end of the agent's lifetime, output exactly one line: `[agent done] tracks: <comma-separated list>` and stop.

---

## Operator usage

### Mode A — explicit assignment (one agent, one track)

For each parallel track:

1. Fill in the Mode A Parameters block (different `TRACK_ID` per agent, different `BRIEF_FILE`).
2. Hand the filled-in superprompt to the agent.
3. Wait for the done-report or blocker.
4. Review the done-report's "Files modified" against the brief's Deliverable list — they must match.
5. After all parallel tracks report done, merge each branch back into `BASE_BRANCH` in turn:

```bash
cd "$REPO_ROOT"
git checkout "$BASE_BRANCH"
for branch in "${BASE_BRANCH}-trackA" "${BASE_BRANCH}-trackB" "${BASE_BRANCH}-trackC"; do
    git merge --no-ff "$branch" -m "merge $branch"
done
```

Conflicts at merge-back time mean a track violated its scope. Reject and re-run with the violation called out.

### Mode B — wave launch (N agents, same prompt, autonomous)

1. Author one brief at `docs/agent-briefs/<area>/<file>.md` with one `### Track <id>` section per id, plus a "universal rules" / "anti-stuck" section every track inherits. Tracks must be mutually independent (no ordering required between them).
2. Fill in the Mode B Parameters block ONCE: `REPO`, `GH_REPO`, `BASE_BRANCH`, `BRIEF_FILE`, `TRACK_PREFIX`, `TRACKS=(F1 F2 …)`, `WORKTREE_PARENT`.
3. Open N CLI sessions (1 ≤ N ≤ |TRACKS|). Paste the same filled-in prompt into each.
4. Walk away. Each agent self-claims, implements, pushes, queues `--auto` merge, loops until claims are exhausted, exits.
5. Required CI checks gate the merges; merges happen as checks go green.

Stuck-agent recovery: `git push origin --delete <branch>` releases the claim; `gh pr close <pr#>` closes any half-baked PR. Then start a fresh agent — it'll re-claim the released id automatically.

### When to pick which mode

| Situation                                                | Mode |
| -------------------------------------------------------- | ---- |
| Tracks have ordering deps (L0 → L1.* → L2)               | A — operator gates each phase |
| One-off scoped task you want to verify before merging    | A — done-report is the gate |
| 5+ mutually independent tracks                           | B — autonomous saves operator time |
| You want auto-merge + per-group push as the audit trail  | B |
| You're debugging the track design itself                 | A — easier to inspect mid-flight |

---

## How to write a brief that's superprompt-compatible

### Mode A brief (one file per track)

Match `docs/agent-briefs/audit/L0-skeleton.md` as a template. Required sections:

1. **Goal** — one sentence.
2. **Phase / blocks-on** — for ordering.
3. **Inputs** — exact file paths, in read-order, that the agent must read in full.
4. **Deliverable** — explicit list of files to create / modify. Files NOT on this list are out-of-scope.
5. **Acceptance** — shell commands. Every one must exit 0. The agent runs them verbatim.
6. **Hard stops** — including a "parallel-scope" subsection that lists the files this track exclusively owns (when running in a parallel family).
7. **Done-report format** — the standard template (or a brief-specific extension).
8. **Commit block** — exact `git commit` invocation, with a HEREDOC commit message.

### Mode B brief (one file with N track sections)

Single file with this shape:

```markdown
# <Area> follow-up tracks

## Universal rules
(applies to every track in this file — file-scope discipline, build verify, push cadence, …)

## Anti-stuck
(decision rules: mirror reference file X; if Y unclear, pick the simpler; …)

## Reference file
(the canonical existing file the agent should mirror when in doubt)

### Track F1: <name>
**Files you own:** <explicit paths>
**Files you may NOT touch:** <forbidden paths if not obvious>
**Reference to mirror:** <path>
**NuGet (if any):** <single allowed package>

<numbered work plan, one logical group per number>

**Done:** `<shell command that returns 0 when this track is done>`

### Track F2: <name>
…

### Track F3: <name>
…
```

The track section must be self-contained — an agent reads ONLY its own track section + the universal/anti-stuck/reference sections, and that's everything it needs.

The "Done" command at the bottom of each track is the gate: build / test / smoke command that exits 0 only when the track's work is correct.

The superprompt enforces the contract; the brief carries the substance.

---

## Why this exists

Earlier in the platform's history, parallel agents stepped on each other's toes via:
- shared DI registration files (one agent's `services.AddX` overwrote another's),
- the same `Program.cs` block (concurrent edits = merge conflict),
- shared `csproj` files (concurrent package additions = merge conflict),
- shared migrations (timestamps collided),
- accidental `git add -A` pulling in another track's untracked work.

The superprompt + the brief's Deliverable + the parallel-scope hard-stops together prevent every one of those by contract:
- Each track owns disjoint files (declared in the brief).
- Each track stages only the files it owns (`git add <explicit-list>`, never `-A`).
- The pre-commit scope check catches any drift.
- Worktrees keep the filesystems separate.

If a merge conflicts at the end, the contract was violated — the superprompt run that violated it is the one to reject and re-run.
