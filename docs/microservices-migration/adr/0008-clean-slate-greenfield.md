# ADR-0008: Clean-Slate Greenfield Build (Supersedes ADR-0007)

**Status:** Accepted
**Date:** 2026-05-02
**Deciders:** chidionyema
**Supersedes:** [ADR-0007: Strangler Fig Migration](./0007-strangler-fig-migration.md)

## Context

ADR-0007 chose a strangler fig migration over 14 weeks, with YARP routing, dual-write phases, soak periods, and feature-flag rollback. That decision was made under the implicit assumption that the existing monolith was a system worth migrating — i.e., one with users, traffic, or some other reason that breakage would matter.

That assumption was wrong. The existing monolith is a **portfolio prototype** with no live users, no traffic, and no rollback urgency. Strangler fig solves a class of problems we don't have. Adopting it means paying its costs (14 weeks instead of 8, YARP infrastructure, `Legacy/` holding pen, dual-write phases, conditional fixtures) for zero benefit.

## Decision

**Build the new microservices system from scratch in a fresh repository.** Treat the existing monolith as a *reference* for domain logic, EF mappings, validators, and security middleware — code is freely cribbed but not migrated. The new repo starts empty.

Success criteria are binary and testable:
1. All tests pass (per service + cross-service contract + E2E).
2. Local deploy works (`dotnet run --project deploy/aspire` and `make k8s-up`).

There is nothing to roll back, no traffic to route, no data to dual-write. A phase ships when its acceptance criteria are met.

The build proceeds in 8 weeks across 8 phases (see [03-build-plan.md](../03-build-plan.md)):

0. Foundation (week 1)
1. identity-svc — vertical slice template (weeks 2–3)
2. catalog-svc (week 4)
3. payments-svc (week 5)
4. orders-svc (week 6)
5. checkout-orchestrator-svc — the saga (week 7)
6. content-svc (weeks 7–8)
7. bff-web (week 8)
8. Polish + case study (week 8)

## Options Considered

| Option | Pros | Cons | Verdict |
|---|---|---|---|
| **Clean-slate greenfield (chosen)** | ~8 weeks instead of 14. Zero migration overhead. Architecture decisions made once, correctly. New repo starts clean — no `Legacy/` holding pen, no YARP routing, no feature flags. | Loses the "I can migrate live systems" portfolio angle. **Mitigation:** the case study explicitly addresses this — "I considered strangler fig and rejected it because there was nothing to strangle." | **Chosen.** Aligns with reality. |
| Strangler fig (per ADR-0007) | Demonstrates real-world migration competence. Each phase is independently revertable (a meaningful guarantee in production systems). | Pays significant cost — extra 6 weeks, YARP infrastructure, dual-write phases, soak periods — for guarantees we don't need. The "competence demonstration" can be addressed by *documenting* that strangler fig was considered and rejected, with reasons. | Rejected by this ADR. |
| Hybrid — start greenfield, then "migrate" the monolith for show | Combines benefits. | Doubles the work for a fictional migration. Reviewers will see through it. | Rejected. |

## Consequences

### Positive
- **6 weeks faster.** Phase 5 (the saga, headline demo) lands at week 7 instead of week 12.
- **No accidental complexity.** Every line of code in the new repo serves the target architecture, not the migration logistics.
- **Cleaner case study.** "Here's how I built it" reads better than "here's how I migrated it" when there was nothing to migrate.
- **Risks 1, 3, 4 from ADR-0007's risk register vanish or shrink** — no saga extraction risk, no outbox replay risk, no cross-DB FK audit risk.
- **All architecture decisions stay valid.** [01-architecture.md](../01-architecture.md), [02-platform.md](../02-platform.md), and ADRs 0001–0006 are unchanged.

### Negative
- **Loses the strangler fig as a portfolio talking point.** Mitigation: the case study includes a section "Why I didn't use strangler fig" that demonstrates the same architectural maturity (knowing *when* to use a pattern).
- **No proof I can do a real migration.** Mitigation: future portfolio piece (separate repo) could be "extract one service from a real legacy system."

### Neutral
- The 14 ADRs and risk docs that referenced strangler-fig phases get updated to reference the new build plan.

## Notes

The existing monolith stays in its current location, untouched, as a permanent reference. See [ADR-0009: Monolith as Reference, Not Source](./0009-monolith-as-reference-not-source.md) for the decision on what role it plays.

The new repo is a separate GitHub project. The architecture documentation under `docs/microservices-migration/` (this directory) gets copied into it as the foundation.

Reference: [03-build-plan.md](../03-build-plan.md)
