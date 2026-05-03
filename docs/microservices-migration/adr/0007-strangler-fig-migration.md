# ADR-0007: Strangler Fig Migration over 14 Weeks (Superseded)

**Status:** Superseded by [ADR-0008: Clean-Slate Greenfield Build](./0008-clean-slate-greenfield.md)
**Date:** 2026-05-02
**Deciders:** chidionyema

> **Why superseded:** This ADR was written under the implicit assumption that the existing monolith was a system worth migrating safely (live traffic, real users, rollback urgency). That assumption was wrong — the existing monolith is a portfolio prototype with no live traffic. Strangler fig solves a class of problems we don't have. ADR-0008 chose a clean-slate greenfield build instead, cutting 6 weeks and removing significant accidental complexity (YARP, Legacy/, dual-write, feature flags). This ADR is preserved as the rejected-but-considered alternative — it documents the path we chose against and why.

## Context

The monolith works today. Tests pass. Local deploy is one command. Customers (or in this case, recruiters running the demo) get value from it. Any migration that breaks this for an extended period is a self-inflicted wound.

Three migration patterns to choose from:
- **Big-bang** — freeze the monolith, build the polyrepo, cut over once.
- **Branch-by-abstraction** — refactor in-place behind interfaces, then swap implementations.
- **Strangler fig** — new services grow alongside the monolith, gradually consuming responsibilities, monolith trunk dies.

## Decision

**Strangler fig over 14 weeks**, with the following structure:

- Each service folder grows alongside `src/Legacy/` (the moved-but-unmodified monolith code).
- YARP at the edge routes per-path traffic between monolith and extracted service. Cutover is a routing config change.
- Each phase: extract one service, soak 7 days, mark done, move on.
- All tests must stay green throughout. Three-strike flake rule halts the active phase.
- Each phase has explicit rollback (YARP route flip, feature flag toggle, or schema reversion).

The 14-week breakdown:

| Phase | Weeks | What |
|---|---|---|
| 0 | 1–2 | Foundation: monorepo skeleton, BuildingBlocks/Contracts NuGets, YARP, Pact broker, ArgoCD, observability stack. |
| 1 | 3–4 | Extract identity-svc (JWT switch to RS256/JWKS). |
| 2 | 5–6 | Extract catalog-svc. |
| 3 | 7 | Pact-everything: 13 events get full producer + consumer Pacts. |
| 4 | 8–9 | Extract payments-svc. |
| 5 | 10 | Physical DB split via logical replication. |
| 6 | 11–12 | Extract orders-svc + checkout-orchestrator-svc. **Crown jewel.** |
| 7 | 13 | Extract content-svc. |
| 8 | 14 | Retire `src/Legacy/`. |

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Strangler fig (chosen)** | Tests stay green throughout. Each phase is independently revertable. New services prove themselves before the monolith trusts them. Best fit for the codebase's existing event surface + per-context outbox + Pact scaffolding. | Long timeline. Requires discipline (path-aware CI, dual-write phases, per-phase rollback rehearsal). | **Chosen.** Only option that honors "tests + local deploy must work after migration." |
| Big-bang | Conceptually simpler. One big PR, one cutover. | Catastrophic for a saga of this complexity. 3-month freeze on monolith. No rollback once cut over. Tests broken for weeks. | Rejected. The saga's complexity (`ProcessCheckoutCommandHandler` with 10+ deps) makes this a one-way door. |
| Branch-by-abstraction (pure) | Great inside a service. Refactor first, swap implementations later. | Doesn't solve repo-split logistics or independent deployability. Doesn't address the data split. | Rejected as the *primary* strategy. **Used inside phases** (e.g., the dual-validation HS256+RS256 middleware in Phase 1). |

## Consequences

### Positive
- Existing test suite (~158 files) stays the safety net. Coverage doesn't drop during migration.
- Each phase delivers visible portfolio value (one new service, new diagrams, new ADR, possibly new blog post).
- Risk surfaces incrementally — saga risk caught in Phase 6, not Phase 1.
- Rollback per phase is well-defined and rehearsable.
- The case study (`docs/case-study/`) writes itself one phase at a time.

### Negative
- 14 weeks is a long commitment. **Mitigation:** each phase is independently meaningful — even partial completion (Phases 0–3) is a portfolio artifact.
- Path-aware CI complexity. **Mitigation:** explicit `dorny/paths-filter` config with per-path job mapping documented in CI workflow.
- Test files temporarily duplicated during cutover (copy first, delete later — see [04-testing-strategy.md § Continuous-Green Strategy](../04-testing-strategy.md#per-test-suite-migration-mapping)). **Mitigation:** `[Trait("Category","CrossService")]` to flag duplicates; PR titled `chore: remove <X> tests, owned by <X>-service repo as of <SHA>` cleans up.

### Neutral
- The migration doc (`docs/microservices-migration/`) is itself a portfolio artifact. **Net positive** — but adds documentation effort.

## Notes

Per-phase universal go/no-go criteria — halt if any tripped:

- p99 latency regression >20% on any user-facing endpoint
- Pact `can-i-deploy` red against production
- Any test suite red on `main` for >24 h
- Error rate in extracted service >0.5% over 1 h sustained
- Vault token rotation failure
- Saga zombie count >0 in nightly synthetic

Per-phase rollback rehearsal: every phase includes a "rollback drill" in its acceptance criteria. We don't ship a phase without proving we can revert it.

Reference: [03-migration-plan.md](../03-migration-plan.md), [05-risks.md § Risk-Driven Phase Sequencing](../05-risks.md#risk-driven-phase-sequencing)
