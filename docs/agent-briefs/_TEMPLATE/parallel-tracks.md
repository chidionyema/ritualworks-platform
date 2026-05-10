# {{FEATURE}} — parallel tracks (Mode B brief)

Mode B brief for the **{{FEATURE}}** feature. Each `### Track Tn` section below is mutually independent and can be claimed by a separate agent. Use this with `docs/agent-briefs/SUPERPROMPT.md` Mode B.

## Wave configuration (read by `wave` tool)

```
REPO=/Users/chidionyema/Documents/code/ritualworks-platform
GH_REPO=chidionyema/ritualworks-platform
BASE_BRANCH=feat/{{FEATURE}}-service
BRIEF_FILE=docs/agent-briefs/{{FEATURE}}/parallel-tracks.md
TRACK_PREFIX=feat/{{FEATURE}}-
TRACKS=(T1 T2 T3 T4)
# Gemini --yolo workspace sandbox blocks /tmp writes. Use ~/.gemini/tmp/<project>
# (allowed by the sandbox) so agents can create their per-track worktrees.
WORKTREE_PARENT=/Users/chidionyema/.gemini/tmp/ritualworks-platform
```

> **Prerequisite:** any L0 scaffold (interfaces, project skeleton, DI orchestrator) MUST be merged into `$BASE_BRANCH` before launching. Tracks can only run in parallel if the contracts they depend on are already stable.

---

## Universal rules

### File-scope discipline (THE contract)

Each track owns a disjoint set of files. **You do not touch files outside your track's "Files you own" list.** Period. Tracks merge cleanly back to `$BASE_BRANCH` only because file scopes don't overlap; one cross-track edit and the merge will conflict.

The only file shared across phases (if any) is `<<path/to/orchestrator>>` — written ONCE by L0, modified by no L1 phase. Each phase fills in its own sibling.

### Build verify per file group

Before every commit, build only the projects your group touched:

```bash
dotnet build "$WT/<your-area>" --nologo --verbosity quiet
```

Must exit 0. If it fails, fix forward; don't commit broken builds.

### Push cadence

Per file group: commit + push immediately. Not "one big commit at the end." This gives the operator visibility and a recovery point per group.

```bash
git -C "$WT" add <explicit list of files in this group>
git -C "$WT" commit -m "<type>({{FEATURE}}-<TRACK>): <one-line summary>"
git -C "$WT" push origin "${TRACK_PREFIX}${TRACK}"
```

## Anti-stuck

If you're stuck for more than 10 minutes on the same problem, take ONE of these escapes (in order):

1. **Mirror the reference file.** Copy the pattern from `## Reference file` (below) before inventing.
2. **Narrow scope.** If a sub-task is fighting you, comment it out with `// TODO({{FEATURE}}-<TRACK>): <what>` and keep going on what compiles.
3. **Cross-track dependency?** Don't make the cross-track change. Write a `// TODO(<sibling-track>)` in YOUR file describing what you need from the sibling, and continue without making it.
4. **Brief is wrong?** STOP. Don't guess. Release the claim (`git push origin --delete "${TRACK_PREFIX}${TRACK}"`) and exit so a human can fix the brief.

## Reference file

`<<path/to/canonical/reference.cs>>`

The existing implementation in this repo that demonstrates the patterns this wave should mirror. When in doubt — names, layering, error handling, test shape — read this file. Do not invent new patterns.

---

### Track T1: <<short name>>

**Files you own (exclusive):**
- `<<path/to/owned-1>>`
- `<<path/to/owned-2>>`

**Files you may NOT touch:**
- `<<paths owned by other tracks>>`
- Any other phase's DI file
- Any project's `.csproj`

**Reference to mirror:** `<<file path>>`

**NuGet (if any):** none

**Spec sections:** §<<X.Y>> of `<<spec file path>>`

**Done:** `<<shell command that exits 0 only when this track is correct>>`

#### Work plan

1. **<<File group 1>>** — <<what to create + why>>. Build, commit, push.
2. **<<File group 2>>** — <<what>>. Build, commit, push.
3. **<<Tests>>** — <<what>>. Run, commit, push.

---

### Track T2: <<short name>>

**Files you own (exclusive):**
- `<<path>>`

**Files you may NOT touch:**
- `<<paths>>`

**Reference to mirror:** `<<file>>`

**NuGet (if any):** none

**Spec sections:** §<<X.Y>>

**Done:** `<<command>>`

#### Work plan

1. <<step>>

---

### Track T3: <<short name>>

**Files you own (exclusive):**
- `<<path>>`

**Files you may NOT touch:**
- `<<paths>>`

**Reference to mirror:** `<<file>>`

**NuGet (if any):** none

**Spec sections:** §<<X.Y>>

**Done:** `<<command>>`

#### Work plan

1. <<step>>

---

### Track T4: <<short name>>

**Files you own (exclusive):**
- `<<path>>`

**Files you may NOT touch:**
- `<<paths>>`

**Reference to mirror:** `<<file>>`

**NuGet (if any):** none

**Spec sections:** §<<X.Y>>

**Done:** `<<command>>`

#### Work plan

1. <<step>>
