# ADR-0009: The Existing Monolith Is a Reference, Not a Source

**Status:** Accepted
**Date:** 2026-05-02
**Deciders:** chidionyema

## Context

[ADR-0008](./0008-clean-slate-greenfield.md) chose a clean-slate greenfield build over a migration. That decision raises an immediate question: what role does the existing monolith play during the build?

Three options:
1. **Source** — selectively port files, with attribution and minimal modification (essentially a slow strangler).
2. **Reference** — open in another tab; read freely; rewrite by hand into the target shape.
3. **Discard** — close the existing repo, never look at it again.

## Decision

**The existing monolith is a reference, not a source.** It stays in its current repo, untouched, never modified, never imported from. Code in the new repo is *typed by hand* (or LLM-assisted from re-reading) into the target architecture's shape.

Specifically:
- **Domain entities** are re-implemented per service. Cribbing the *invariants* and *behavior* from the monolith's `src/Domain/Entities/` is fine; copy-pasting the file is not (because the file lives in the wrong namespace, with the wrong base class, in the wrong project structure).
- **EF migrations** are generated fresh per service. The old monolith's migrations are reference for *what columns + indexes were needed*, not files to copy.
- **Validators, MediatR handlers, command/query records** are re-written per service. The monolith's logic is the reference; the new code lives in the new structure.
- **Tests are NOT migrated.** They are re-written against the new service. The old test files are reference for *what cases need coverage*. The 158-file inventory in [04-testing-strategy.md](../04-testing-strategy.md) is a checklist, not a migration manifest.
- **Cross-cutting infrastructure** (`Result<T>`, `IDomainEvent`, MediatR behaviors, Vault interceptor, MT outbox wiring) is **lifted into `Haworks.BuildingBlocks`** as a deliberate package. This is the only "porting" allowed — and it ports into a *new* package, not a parallel one.

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Reference only (chosen)** | Forces every line of the new code through "is this still correct?" thinking. Catches dead code, bad abstractions, and outdated patterns. The new repo's git log is honest — every commit is genuinely new work. | Slower than copy-paste. | **Chosen.** The friction is the point. |
| Source — selectively port files | Faster. Less re-typing. | Drags monolith design choices forward unintentionally. The new repo's git log lies (commits look like authoring; reality is porting). The "re-evaluation pressure" is lost. | Rejected. |
| Discard — never look at the monolith again | Cleanest start. No monolith bias. | Wastes the validation we already have. The monolith's domain model, edge cases, and security middleware are working code — re-deriving them from scratch costs weeks for no benefit. | Rejected. The monolith is too valuable as a reference to throw away. |

## Consequences

### Positive
- The new repo's git log is genuine — every commit is real work, not file moves.
- Re-reading the monolith forces "do we still want this?" thinking. We catch over-engineering, dead code, premature abstractions.
- The case study can honestly say "I built X from scratch, informed by Y." Not "I ported X from Y."
- The monolith stays runnable as-is for any "remind me how this used to work" investigation.

### Negative
- Slower than copy-paste. **Mitigation:** acceptable cost; the friction is what makes the rewrite better than the original.
- Risk of forgetting to crib something important from the monolith (e.g., a subtle validation rule). **Mitigation:** Phase N's acceptance criteria include "compare new code against monolith counterpart for behavioral parity" before merging.

### Neutral
- The monolith repo can be archived (read-only) or kept active for reference. Either is fine. Recommended: leave it active until the new system is feature-complete; archive after Phase 8.

## Notes

This decision applies to **code**, not to **knowledge**. We freely use:
- The monolith's CLAUDE.md and `.claude/rules/` as the foundation of the new repo's conventions.
- The monolith's documentation under `docs/architecture/`, `docs/infrastructure/`, `docs/runbooks/` as reference for "how things were intended to work."
- The monolith's test inventory as a coverage checklist.
- The monolith's incident/bug history (in git log) as a "what to watch out for" guide.

Knowledge is portable; code is not.

Reference: [03-build-plan.md](../03-build-plan.md), [04-testing-strategy.md](../04-testing-strategy.md)
