# Gemini CLI agent protocol — Search service

You are a Gemini CLI agent. Read this file **first**, then read the assigned brief (`B<n>-*.md`), then read the brief's **Inputs** section, then start work.

## How a brief is structured

Every brief has these sections, in this order:

1. **Goal** — one sentence.
2. **Phase / blocks-on** — which phase you're in and which prior briefs must be done.
3. **Inputs** — exact file paths to read, in order. **Read these all before writing any code.** Don't grep blindly.
4. **Deliverable** — files to create or modify. Concrete checklist; don't go beyond it.
5. **Acceptance** — shell commands that must pass. **Non-negotiable.** Run them yourself before reporting done.
6. **Hard stops** — explicit "do not do X". Common rules:
   - Do not edit files outside the listed paths.
   - Do not introduce new top-level NuGet packages without checking existing csproj files for similar deps.
   - Do not change CI workflow files unless the brief says so.
7. **Done-report format** — paste back exactly this template, filled in.

## Anti-spiral rules

These exist because LLM coding agents commonly fail in predictable ways. Internalize them.

- **Read Inputs first.** Most failures come from skimming Inputs and grepping the wrong things.
- **30-minute time budget.** If you're 30 min in and not approaching green, stop. Emit a blocker (format below). Do not keep retrying the same approach.
- **No cross-brief edits.** If B5 needs the Catalog HTTP client (B4's territory) to do something it doesn't currently do, you do **not** patch B4's code. You file a blocker.
- **No silent scope expansion.** If you spot a refactor that "would only take a minute," do not do it. Note it in the done-report under "out-of-scope observations."
- **Trust but verify the spec.** If the spec says X but the code says Y, the spec is the source of truth — but pause and call it out in the done-report. Don't silently rewrite the spec; don't silently break from it.
- **Don't fabricate paths.** If a file the Inputs list claims exists doesn't, file a blocker. Don't invent.

## Done-report format

Paste this verbatim, filled in:

```
## Brief B<n> — done

### Files created
- path/to/new/file.cs
- ...

### Files modified
- path/to/existing/file.cs (added X)
- ...

### Acceptance
- `<command 1>`: ✓ / ✗
- `<command 2>`: ✓ / ✗

### Out-of-scope observations
(things you noticed that need follow-up, but did NOT touch)
- ...

### Blockers
(empty if none, otherwise: what failed, what you tried, what you need)
- ...
```

## Blocker format (if you can't finish)

Stop and emit:

```
## Brief B<n> — BLOCKED

### What I tried
- step 1 (cmd / file edit) → outcome
- step 2 → outcome

### Where I'm stuck
(one paragraph, no waffle)

### What I need
(specific: a file's content, a missing dependency, a clarification on the spec)

### Files left in flight
(uncommitted edits the reviewer will want to see)
- ...
```

The reviewer responds, you resume.

## Reviewer's job

After every done-report, a human (or Claude) verifies:
1. Acceptance commands actually pass on a fresh checkout.
2. Files modified match the brief's Deliverable list (no extras).
3. Out-of-scope observations are sensible.

Only after sign-off does the next phase's briefs go out.
