# Wave — Parallel-Agent Protocol & Tooling

**One operator. One spec file. N agents work in parallel and ship the feature.**

This document explains the wave system: a stress-tested protocol that lets multiple autonomous coding agents (Gemini, Claude, etc.) self-organise around a feature, claim non-overlapping work, and merge it without operator coordination.

## TL;DR

```bash
docs/agent-briefs/wave run docs/agent-briefs/audit-service-spec.md
```

That's the whole operator workflow. The tool will:
1. Read the spec.
2. Generate a `parallel-tracks.md` brief decomposing the work into 3–5 disjoint tracks.
3. Validate the brief.
4. Launch N Gemini agents in tmux panes.
5. Each agent atomically claims a track, builds, tests, opens a PR, and auto-merges.

## How it works

### The contract

Every wave is built on three guarantees enforced by `SUPERPROMPT.md` Mode B:

| Guarantee | How it's enforced |
|---|---|
| **No two agents on the same track** | Atomic claim: `git push origin refs/remotes/origin/$BASE:refs/heads/$BRANCH`. First-writer-wins; concurrent losers fall through to the next track. |
| **No two agents touching the same files** | Each track's brief declares `**Files you own (exclusive):**`. Crossing that boundary is a scope violation and the agent's PR will be rejected at merge. |
| **Dead claims self-heal** | Sentinel commit immediately after claim → liveness signal. Future waves detect 0-commits-beyond-base + confirm-window expiry → reclaim. |

### The components

```
docs/agent-briefs/
├── SUPERPROMPT.md           # the protocol (Mode A single-track, Mode B autonomous wave)
├── wave                     # operator CLI
├── _TEMPLATE/
│   └── parallel-tracks.md   # fillable scaffold
├── README.md                # quickstart
└── <feature>/
    └── parallel-tracks.md   # generated per-feature wave brief
```

### The phases

```
operator                                    │  agents
                                            │
1. wave run <spec>                          │
   └─ design pass (gemini reads spec +      │
      protocol + example, writes brief)     │
   └─ validate brief                        │
   └─ launch N agents in tmux ──────────────┼─→ each agent: read protocol
                                            │   ├─ atomic claim a track
                                            │   ├─ fetch local tracking ref
                                            │   ├─ create worktree, sentinel commit
                                            │   ├─ work in scope, per-group commits
                                            │   ├─ Done check
                                            │   ├─ open auto-merge PR
                                            │   └─ loop to next unclaimed track
                                            │
2. wave status <feature>  ←─────────────────┴─ branches + PRs visible on origin
3. wave fix-stalled <feature>  (if needed: open PRs for branches stuck before Step 5)
4. (operator) review + merge integration branch → main
```

## Protocol invariants (stress-tested)

The protocol is verified by `/tmp/stress-claim/stress.sh` (10/10 invariants pass). The five invariants:

| | Invariant | Test |
|---|---|---|
| **INV1** | Race-safe claim — N agents racing K tracks → exactly K unique winners | 16 concurrent agents, 5 tracks: 5 claims, 0 collisions |
| **INV2** | Zombie reclaim — branch on origin with 0 commits beyond base + no live agent → next wave reclaims | Hand-planted dead branch, fresh agent reclaims it |
| **INV3** | Live respect — branch with sentinel commit (≥1 beyond base) → not reaped | Sentinel-only branch left untouched |
| **INV4** | Worktree tracking — post-claim worktree's upstream resolves correctly | All worktrees: no "upstream is gone" |
| **INV5** | Race window — slow agent (delay between claim and sentinel) protected by `ZOMBIE_CONFIRM_SECS` | 5s slow agent, 8s confirm window: survives |

## Operator's irreducible job

The wave system handles claim, scope, isolation, worktrees, sentinels, reclaim, PRs, and auto-merge. **The operator's job reduces to:**

1. **Write a spec** — what the feature is, what it does, what "done" looks like.
2. **Run `wave run <spec>`** — design + launch.
3. **Watch `wave status <feature>`** — drink coffee.
4. **Merge the integration branch to main** when all tracks have merged.

## Subcommands reference

| Command | Purpose |
|---|---|
| `wave run <spec>` | Design + validate + launch (the typical entrypoint) |
| `wave run <feature> <spec> --yes` | Same, skip the launch confirmation prompt |
| `wave init <feature>` | Scaffold a brief from `_TEMPLATE/` (manual workflow) |
| `wave validate <feature>` | Check brief well-formedness (parameters block, required H2 sections, per-track labelled lines) |
| `wave prompt <feature>` | Print the filled-in Mode B prompt to stdout |
| `wave launch <feature> [N]` | Spawn N gemini agents in tmux (default = #tracks) |
| `wave attach <feature>` | tmux attach to the wave session |
| `wave status [feature]` | Per-track dashboard: branch state + PR state + activity |
| `wave fix-stalled <feature>` | Open PRs for branches with commits but no PR (handles agents that died after Step 4 but before Step 5) |
| `wave clean <feature>` | List dead worktrees + zombie reclaim suggestions |

## What makes a feature parallelizable?

Not every feature decomposes into a wave. The system requires:

1. **L0 scaffold merged.** Stable interfaces, project skeletons, DI orchestrator must already be on the integration branch. Without this, parallel tracks would race on contract definitions.
2. **Disjoint file scopes.** Each track must own files no other track touches. Shared files (DI orchestrator, csproj) get written ONCE by L0 and frozen.
3. **A reference implementation exists.** A canonical file in the repo whose patterns each track mirrors. This is what stops agents from inventing divergent styles.
4. **Per-track Done check.** A shell command that exits 0 only when that track is correct. Usually `dotnet test --filter "FullyQualifiedName~..."`.

If any of these fail, the design pass should refuse and tell the operator to do an L0 scaffold first.

## Wave brief structure (what `wave run` generates)

```markdown
# <feature> — parallel tracks (Mode B brief)

## Wave configuration
```
REPO=...
GH_REPO=...
BASE_BRANCH=feat/<feature>-service
BRIEF_FILE=docs/agent-briefs/<feature>/parallel-tracks.md
TRACK_PREFIX=feat/<feature>-
TRACKS=(T1 T2 T3 T4)
WORKTREE_PARENT=/tmp
```

## Universal rules
[file-scope discipline, build verify, push cadence]

## Anti-stuck
[escape rules: mirror reference, narrow scope, cross-track TODO, release claim]

## Reference file
`<canonical/file/path.cs>`

### Track T1: <name>
**Files you own (exclusive):** ...
**Files you may NOT touch:** ...
**Reference to mirror:** ...
**NuGet (if any):** none
**Done:** dotnet test ...

### Track T2: ...
```

## Failure modes & recovery

| Symptom | Cause | Recovery |
|---|---|---|
| Branch on origin, 0 commits beyond base, no agent | Agent died before sentinel commit | Next wave's zombie detector reclaims it after `ZOMBIE_CONFIRM_SECS` (default 90s) |
| Branch has commits but no PR open | Agent died after Step 4 but before Step 5 (PR creation) | `wave fix-stalled <feature>` |
| Two agents both wrote the same file | Scope violation in the brief | Reject one PR; fix the brief; relaunch |
| Worktree status: "upstream is gone" | Old script: push didn't update local tracking ref | Patched in `1fb55a2` — fetch tracks the new branch immediately after claim |
| All agents exit "all tracks claimed or merged" | Previous wave left empty zombie branches | Old script: manual cleanup; patched script: zombie auto-reclaim |

## Implementation history

The protocol grew through three commits on `feat/audit-service`:

- **`1fb55a2`** — liveness-aware claim (sentinel), proper local tracking, zombie reclaim
- **`483c541`** — re-confirm zombies before reaping (race-window protection)
- **`a4c96e7`** — wave tool (init/validate/prompt/launch/status/fix-stalled/clean)

The audit feature was the first end-to-end test: 4 tracks, 4 PRs, all merged.

## Quick reference

```bash
# new feature, end-to-end
docs/agent-briefs/wave run docs/agent-briefs/<feature>-spec.md

# already have a brief, just launch
docs/agent-briefs/wave launch <feature>

# check what's running
docs/agent-briefs/wave status

# unstick stalled branches
docs/agent-briefs/wave fix-stalled <feature>

# stress-test the protocol (paranoia, not routine)
/tmp/stress-claim/stress.sh
```
