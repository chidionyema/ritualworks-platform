# Gemini CLI agent protocol — Audit service

You are a Gemini CLI agent. Read this file **first**, then read the assigned brief (`L<n>-*.md`), then read the brief's **Inputs** section, then start work.

The authoritative design for everything below is `docs/agent-briefs/audit-service-spec.md`. Per-phase briefs reference sections of it. When the spec and a brief disagree, the spec wins; pause and call it out.

## How a brief is structured

Every brief has these sections, in this order:

1. **Goal** — one sentence.
2. **Phase / blocks-on** — which phase you're in and which prior briefs must be done.
3. **Inputs** — exact file paths to read, in order. **Read these all before writing any code.** Don't grep blindly.
4. **Deliverable** — files to create or modify. Concrete checklist; don't go beyond it.
5. **Acceptance** — shell commands that must pass. **Non-negotiable.** Run them yourself before reporting done.
6. **Hard stops** — explicit "do not do X".
7. **Done-report format** — paste back exactly the template below, filled in.

## Anti-spiral rules

- **Read Inputs first.** Most failures come from skimming Inputs and grepping the wrong things.
- **45-minute time budget per phase.** If you're 45 min in and not approaching green, stop. Emit a blocker (format below). Do not keep retrying the same approach.
- **No cross-phase edits.** L1.B does not patch L1.A's extractors; if L1.A is wrong, file a blocker.
- **No silent scope expansion.** If you spot a refactor that "would only take a minute," do not do it. Note it in the done-report under "out-of-scope observations."
- **Trust but verify the spec.** If the spec says X but the code says Y, the spec is the source of truth — but pause and call it out. Don't silently rewrite the spec; don't silently break from it.
- **Don't fabricate paths.** If a file the Inputs list claims exists doesn't, file a blocker. Don't invent.
- **Commit per phase.** Each phase ends with a commit. The next phase rebases nothing — it starts from the previous commit.
- **No solution-wide builds.** `dotnet build RitualworksPlatform.sln` is forbidden — too risky on origin/main where unrelated WIP can break it. Build only the projects in the brief's Deliverable list, plus `deploy/aspire/RitualworksPlatform.AppHost.csproj` to verify wiring.

## Done-report format

Paste this verbatim, filled in:

```
## Brief L<n> — done

### Files created
- path/to/new/file.cs
- ...

### Files modified
- path/to/existing/file.cs (added X)
- ...

### Acceptance
- `<command 1>`: ✓ / ✗
- `<command 2>`: ✓ / ✗

### Commit
- <short hash> <subject line>

### Out-of-scope observations
(things you noticed that need follow-up, but did NOT touch)
- ...

### Blockers
(empty if none, otherwise: what failed, what you tried, what you need)
- ...
```

## Blocker format (if you can't finish)

```
## Brief L<n> — BLOCKED

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

## Phase order — parallel model

```
L0 (sequential, must finish first)
└─> L1.A   ┐
└─> L1.B   ├── all four run IN PARALLEL on separate branches off L0
└─> L1.C   │
└─> L1.D   ┘
```

L0 lays the DI foundation so the four L1 phases never touch the same file. Specifically:

| Phase | Owns these files exclusively                                                                                                          |
| ----- | --------------------------------------------------------------------------------------------------------------------------------------- |
| L1.A  | `src/Audit/Audit.Application/Extraction/**`, `src/Audit/Audit.Application/Redaction/**`, `src/Audit/Audit.Application/DependencyInjection.Extractors.cs`, `tests/Audit.Unit/Extraction/**`, `tests/Audit.Unit/Redaction/**` |
| L1.B  | `src/Audit/Audit.Application/Capture/**`, `src/Audit/Audit.Infrastructure/Persistence/AuditWriter.cs`, `src/Audit/Audit.Infrastructure/Migrations/<ts1>_AddAuditEventsPartitioned.*`, `src/Audit/Audit.Application/DependencyInjection.Capture.cs`, `tests/Audit.Integration/EndToEndCaptureTests.cs`, `tests/Audit.Integration/IdempotencyTests.cs` |
| L1.C  | `src/Audit/Audit.Application/Queries/**`, `src/Audit/Audit.Api/Controllers/AuditController.cs`, `src/Audit/Audit.Application/DependencyInjection.Queries.cs`, `tests/Audit.Integration/QueryApiTests.cs` |
| L1.D  | `src/Audit/Audit.Application/Export/**`, `src/Audit/Audit.Infrastructure/Export/**`, `src/Audit/Audit.Infrastructure/Partitions/**`, `src/Audit/Audit.Infrastructure/Migrations/<ts2>_AddAuditExportJobs.*`, `src/Audit/Audit.Api/Controllers/AuditExportController.cs`, `src/Audit/Audit.Application/DependencyInjection.Export.cs`, `tests/Audit.Integration/ExportJobTests.cs`, `tests/Audit.Integration/PartitionRolloverTests.cs` |

**Hard rule:** an L1 phase that needs to touch a file outside its column files a blocker. The single shared file is `src/Audit/Audit.Application/DependencyInjection.cs` — L0 writes it once with calls to `AddAuditExtractors`, `AddAuditCapture`, `AddAuditQueries`, `AddAuditExport`; the four L1 phases never modify it.

### Running in parallel

After L0 lands on `feat/audit-service`, fan out four parallel branches off it:

```bash
cd /Users/chidionyema/Documents/code/rw-audit
git checkout feat/audit-service
git pull --ff-only origin feat/audit-service

# Spawn 4 worktrees, one per phase
for phase in L1A L1B L1C L1D; do
    git worktree add "/Users/chidionyema/Documents/code/rw-audit-$phase" \
        -b "feat/audit-service-$phase" feat/audit-service
done
```

Then run each `PROMPT-L1<X>.md` against its own worktree. They commit on their own branch; merge order doesn't matter (all four merge cleanly back to `feat/audit-service` because their file scopes are disjoint).

### Merging the four parallel branches

```bash
cd /Users/chidionyema/Documents/code/rw-audit
git checkout feat/audit-service

# Merge each phase as a fast-forward or no-ff merge commit
for phase in L1A L1B L1C L1D; do
    git merge --no-ff "feat/audit-service-$phase" \
        -m "merge feat/audit-service-$phase — audit phase $phase"
done

# Run the full integration suite once everything is on one branch
dotnet test tests/Audit.Integration/Audit.Integration.csproj -c Release
```

If a merge conflicts, that's a contract violation by one of the L1 phases — the file scope discipline failed. Reject the offending phase and re-run with the violation called out.

### Why not even more parallelism?

L0 cannot be parallelised — every L1 phase needs the project skeleton, the DI extension method stubs, and the empty test projects to exist before they can land their own files. L0 is short (~2h of agent work) so the serial cost is small.
