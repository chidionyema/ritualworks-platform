# Gemini CLI agent protocol — platform completion (Phase A + Phase B)

Same protocol as `docs/agent-briefs/search/README.md` and `docs/agent-briefs/checkout/README.md`. Repeated here so this directory is self-contained.

You are a Gemini CLI agent. Read this file first, then read the assigned brief (`A<n>-*.md` or `B<n>-*.md`), then read the brief's **Inputs** section, then start work.

## How a brief is structured

1. **Goal** — one sentence.
2. **Phase / blocks-on** — which prior briefs must be merged into `main` before yours can run.
3. **Inputs** — exact file paths to read first, in order. **Do not grep blindly.**
4. **Deliverable** — files to create/modify; concrete checklist.
5. **Acceptance** — shell commands that must pass.
6. **Hard stops** — explicit do-not list.

## Anti-spiral rules

- **Read Inputs first.** Skim → wrong things grepped → blocker.
- **30-minute time budget.** Stuck past 30 min → emit a blocker per the format below.
- **No cross-brief edits.** If A2 thinks it needs A1's helpers tweaked, file a blocker — don't fix.
- **No silent scope creep.** Spotted a refactor? Note it under "out-of-scope observations". Do not implement.
- **Don't fabricate paths.** If a listed Input doesn't exist, file a blocker.
- **Trust the spec.** If the spec says X but the code says Y, the spec is the source of truth — but pause and call it out in the done-report.

## Done-report format

```
## Brief <ID> — done

### Files created
- path/to/new/file.cs

### Files modified
- path/to/existing/file.cs (added X)

### Acceptance
- `<command 1>`: ✓ / ✗
- `<command 2>`: ✓ / ✗

### Out-of-scope observations
(things you noticed that need follow-up, but did NOT touch)

### Blockers
(empty if none, otherwise: what failed, what you tried, what you need)
```

## Blocker format (if you can't finish)

```
## Brief <ID> — BLOCKED

### What I tried
- step 1 (cmd / file edit) → outcome

### Where I'm stuck
(one paragraph, no waffle)

### What I need
(specific: a file's content, a missing dependency, a clarification)

### Files left in flight
(uncommitted edits the reviewer will want to see)
```

## Phase ordering

```
Phase A — JWT auth + user-id propagation
  A1  BuildingBlocks helpers                   [SEQUENTIAL BLOCKER]
  A2  Per-service JWT wiring                   [PARALLEL after A1]
  A3  BFF forwarding handler                   [PARALLEL after A1]
  A4  Migrate existing user-aware controllers  [PARALLEL after A1]

Phase B — Reservation lifecycle (after Phase A merged)
  B1  StockReservation domain refactor         [SEQUENTIAL BLOCKER]
  B2  Sync reservation HTTP endpoints          [PARALLEL after B1]
  B3  ReservationSweeperService                [PARALLEL after B1]
```

A1 must merge before A2/A3/A4 run.
B1 must merge before B2/B3 run.
**Phase A must fully merge before Phase B starts** (B1 reuses some auth wiring for user-aware reservation endpoints).

## Reviewer's job

After every done-report, the reviewer:
1. Re-runs Acceptance commands on a fresh checkout.
2. Verifies file list matches Deliverable (no extras).
3. Reads "out-of-scope observations".
4. Only then merges.
