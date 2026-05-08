# Gemini CLI agent protocol — checkout & payments gap fills

You are a Gemini CLI agent. Read this file first, then read the assigned brief (`C<n>-*.md`), then read the brief's **Inputs** section, then start work.

This document is a near-clone of `docs/agent-briefs/search/README.md` — the protocol is the same, repeated here so the briefs are self-contained.

## How a brief is structured

1. **Goal** — one sentence.
2. **Phase / blocks-on** — usually `Phase 1, blocks-on: nothing` (these four briefs all run in parallel off `main`).
3. **Inputs** — exact file paths to read first, in order. **Do not grep blindly.**
4. **Deliverable** — files to create/modify; concrete checklist.
5. **Acceptance** — shell commands that must pass.
6. **Hard stops** — explicit do-not list.
7. **Done-report format** — paste back exactly the template in this file.

## Anti-spiral rules

- **Read Inputs first.** Skim → wrong things grepped → blocker.
- **30-minute time budget.** Stuck past 30 min → emit a blocker per the format below.
- **No cross-brief edits.** If C2 thinks it needs to change C3's `DependencyInjection.cs`, file a blocker.
- **No silent scope creep.** Spotted a refactor? Note it in "out-of-scope observations". Do not implement.
- **Trust but verify the spec.** If the spec says X but the code says Y, the spec is the source of truth — but pause and call it out in the done-report.
- **Don't fabricate paths.** If a listed Input doesn't exist, file a blocker.

## Done-report format

```
## Brief C<n> — done

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
## Brief C<n> — BLOCKED

### What I tried
- step 1 (cmd / file edit) → outcome
- step 2 → outcome

### Where I'm stuck
(one paragraph, no waffle)

### What I need
(specific: a file's content, a missing dependency, a clarification)

### Files left in flight
(uncommitted edits the reviewer will want to see)
```

## Reviewer's job

After every done-report, the reviewer:
1. Re-runs the acceptance commands on a fresh checkout.
2. Verifies file list matches Deliverable (no extras).
3. Reads "out-of-scope observations" for follow-up tasks.
4. Only then merges the agent's branch.
