# 05 — Top Risks (Greenfield Build)

Greenfield removes a category of risks (cutover, dual-write, replay) that a real-world migration would face. What's left is **architecture and execution risk** — the things that can go wrong when building from scratch without a safety net.

Honest assessment, ranked by impact × probability.

---

## Risk 1 — Building the Saga Correctly the First Time

**Probability:** medium. **Impact:** high.

The CheckoutSaga is the crown jewel. Get it wrong (incorrect compensation paths, missing idempotency, broken correlation, wrong timeout semantics) and the portfolio's headline demo crashes on stage. The existing monolith's `CheckoutSaga.cs` is a useful reference but has known issues (`SendAsync` to hard-coded queue names, correlate-by-`OrderId` for late events, no explicit `SagaId` in `StockReleasedEvent`).

### Tripwires
- Saga zombie count >0 in 1000-checkout synthetic.
- Mean saga completion time >2 s in happy path.
- Compensation chaos test (`SagaCompensationChaosTests`) red or flaky.
- Dead-letter queue depth on saga queues >0 sustained.

### Mitigations
- **Read MT saga docs end-to-end before Phase 5.** This isn't optional — saga state machines have non-obvious correctness traps (e.g., the difference between `Initially` and `DuringAny`).
- **Build the saga as a state machine first, deploy infrastructure second.** Stand it up in a unit-test harness with `MassTransit.Testing` and prove every transition + compensation path before wiring it to real RabbitMQ.
- **Add `SagaId` to every event the saga consumes** — `Haworks.Contracts.Catalog.v1.StockReservedEvent`, `StockReservationFailedEvent`, `StockReleasedEvent`, `PaymentSessionCreatedEvent`, etc. — and correlate exclusively by `SagaId`. Done in Phase 5 via Contracts package additions.
- **Replace the monolith's `SendAsync(new Uri("queue:..."))` pattern with `Publish`** in the new saga — hard-coded queue names will fail in the polyrepo's per-service vhost world if we ever go that direction.
- **Chaos test is the acceptance gate, not an afterthought.** `SagaCompensationChaosTests` must pass *before* the phase merges, not after.

---

## Risk 2 — Pact Contracts Drift From Runtime Reality

**Probability:** high. **Impact:** high.

Pacts assert *schema*, not *semantics*. A producer can add a field that's syntactically additive but semantically broken — a consumer reads `null` where it used to read a value, silently degrades. The Pact suite stays green; production breaks.

### Tripwires
- Production error rate spike with no failed CI builds in the prior 24 h.
- Consumer-side log volume spike for `NullReferenceException` or `default(T)` paths.
- A consumer's "consumed message → expected business outcome" metric drops.

### Mitigations
- **Every Pact pairs with one chaos test in the consumer** that exercises the real broker round-trip with a deliberately-stale producer schema. Asserts the consumer either adapts or fails loudly — never silently degrades.
- **`can-i-deploy` is a hard PR gate**, not advisory. No `--ignore-failures`.
- **Per-event "business meaning" comment** in the contract record itself — `// PaymentCompletedEvent.Status='Settled' means customer was charged AND funds were captured. Not just authorized.` Forces the producer to think about semantics.
- **Quarterly contract review** — read every Pact, verify it still matches what the producer means. Discipline, not tooling.

---

## Risk 3 — Aspire Composition Breaks Across the Monorepo

**Probability:** medium. **Impact:** medium.

Aspire's `AddProject<T>` was designed for a single-`.sln` world. Our monorepo has 7 separate `.sln`s. The naive port (`AddProject<Projects.Catalog_Api>` from a different sln) fails silently — project type doesn't load, or works inconsistently across machines.

### Tripwires
- `dotnet run --project deploy/aspire` fails on a fresh checkout with cryptic errors.
- Aspire dashboard shows resource missing or unhealthy.
- "I can't run X locally" tickets.

### Mitigations
- **Default to `AddContainer(image-tag)`** in the AppHost. Reserve `AddProject<T>` for the `--override` case.
- **`--override <svc>=local`** pattern — single-service-from-source, rest from images. Image digests pinned in `infra/image-digests.lock` (committed; updated by Renovate).
- **`make refresh-images`** target — pulls latest images, updates lock file. Devs run before/after long branches.
- **`dotnet run --project deploy/aspire --validate`** mode — fails if any service is referenced as a project unintentionally.
- **`docs/runbooks/local-dev-troubleshooting.md`** — fix-it-yourself guide for common failure modes.

This is risk #1 to validate in Phase 0. If the AppHost can't compose 7 service folders cleanly, we know in week 1, not week 7.

---

## Risk 4 — Trace Context Drops at the Outbox Boundary

**Probability:** medium. **Impact:** medium.

The MassTransit outbox persists messages in the producer's database, then a separate process publishes them to RabbitMQ. If the publisher isn't the same `Activity` context as the originator, the W3C `traceparent` header is lost. The trace breaks at every async hop. We discover this when production has a problem and we can't see it.

### Tripwires
- Synthetic test "publish A, consume B, assert same trace ID" red.
- Traces in Tempo show gaps where async hops should connect.
- "Why does my distributed trace stop at the publisher?" support questions.

### Mitigations
- **`Haworks.BuildingBlocks.AddMessaging<TDbContext>()` registers OTel non-optionally** — services cannot register MT without instrumentation.
- **Synthetic CI test:** `tests/Observability/TracePropagationTests.cs` (reference: existing test in monolith's `tests/haworks.Tests.integration/Observability/TracePropagationTests.cs`). Lifted into BuildingBlocks tests, runs on every PR.
- **Per-event correlation header in MT envelope** — `CorrelationId` and `ConversationId` always present, asserted by architecture test.

---

## Risk 5 — Scope Creep / Project Abandonment

**Probability:** the highest of any risk on this list. **Impact:** total.

This is a portfolio project for a single engineer. The biggest threat is not technical — it's the temptation to (a) add an 8th service "because it would be cool to show," (b) fall down a rabbit hole optimizing one area for two weeks, (c) lose momentum after the first 3 services and never finish.

### Tripwires
- Phase X is two weeks late with no concrete acceptance criterion missed (= scope creep).
- Tests for service N are still being written when service N+1 is supposed to start.
- `git log --since="2 weeks ago"` shows no merges.

### Mitigations
- **The 8-week plan is the contract.** Each phase ships when its acceptance criteria are met — not when "it's perfect."
- **Tag every phase done** with `git tag phase-N-complete`. Visible progress.
- **Commit weekly to the case study** (`docs/case-study/`) even if just rough notes. The act of writing forces shipping.
- **The headline demo is the saga (Phase 5).** Once that's done, the project is "shareable enough." Phases 6–8 are polish.
- **Permission to ship something imperfect.** A done v1 with rough edges beats a perfect v0 that never ships.

---

## Risk 6 — Test Infrastructure Drift Across Services

**Probability:** high. **Impact:** low-medium.

Each service has its own `tests/<Service>.Integration/` folder with a Testcontainers fixture. They diverge — one pins `postgres:16`, another pins `postgres:13`. CI behavior depends on which service ran last.

### Tripwires
- "Works on my machine" tickets.
- CI for service A passes; CI for service B fails on the same shared `BuildingBlocks` change.
- Renovate PRs rejected because "this image isn't pinned in our service."

### Mitigations
- **`Haworks.Testing.Containers` NuGet** — pinned image tags in one place. Each service's `*.Integration` project depends on it; no copy-paste of `TestcontainersInfrastructure.cs`.
- **Renovate-bot bumps pinned tags in one place** — one PR updates all services.
- **Per-service test fixture is a 5-line subclass** — `protected override Containers Required => Containers.Postgres | Containers.Redis;` — nothing else.
- **CI matrix on BuildingBlocks change** runs all services' integration suites → drift caught the same day.

---

## Risk 7 — Building the Wrong Abstraction in `BuildingBlocks`

**Probability:** medium. **Impact:** medium.

The shared NuGet packages (`Haworks.BuildingBlocks`, `Haworks.Contracts`) are written in Phase 0 and 1, when only one service exists. By Phase 5, the abstractions might not fit what services 4–7 actually need. The temptation: bend services to fit the abstraction, instead of fixing the abstraction.

### Tripwires
- Service N adds wrapper code to fit the BuildingBlocks API.
- A phase introduces "BuildingBlocks doesn't quite work here, hacking around it."
- Multiple services have the same workaround for a missing BuildingBlocks feature.

### Mitigations
- **Treat BuildingBlocks as draft until Phase 3.** Phases 1–2 (identity + catalog) are when the abstractions get exercised by their first real consumers — refactor freely.
- **No BuildingBlocks API ships v1.0.0 until 3 services depend on it.** Until then it's `0.x.y` — breaking changes allowed.
- **Each phase reviews BuildingBlocks usage:** "did we add wrapper code? Did we copy-paste? Does the abstraction need to change?" Findings go in the phase retrospective in the case study.

---

## Risks We're Choosing to Accept

These are real but not mitigated. Calling them out for transparency.

- **kind cluster on a laptop ≠ EKS.** Some cloud-specific behavior (LoadBalancer provisioning, IAM-based auth) won't exercise locally. Acceptable: docs call out the difference; manifests are written EKS-shape.
- **Self-hosted Pact broker has no SLA.** If it dies, no `can-i-deploy` checks. Acceptable: docker-compose; restore <2 min from KV backup.
- **Single-cluster HA Postgres is not multi-region failover.** Acceptable: portfolio project, not a Tier-1 SaaS.
- **No per-service CI runners.** Single GitHub Actions runner per repo. Acceptable: fast enough at this scale.
- **No real-world load.** Performance tests use synthetic load (k6); we don't know how the system behaves under hostile traffic patterns. Acceptable for portfolio scope; documented as a future-work item in the case study.

---

## Risk-Driven Phase Sequencing

The build plan in [03-build-plan.md](./03-build-plan.md) is sequenced to surface high-impact risks **early**:

| Phase | Risk surfaced |
|---|---|
| 0 (Foundation) | Risk 3 (Aspire monorepo composition). If we can't compose 7 empty service folders, we can't compose 7 real ones. |
| 1 (identity-svc, the template) | Risk 4 (trace propagation), Risk 7 (BuildingBlocks abstraction quality). |
| 2 (catalog-svc) | Risk 6 (test infra drift) — second consumer of `Haworks.Testing.Containers`. |
| 3 (payments-svc) | Risk 2 (Pact drift) — payments produces 6+ events that orders + saga will consume. |
| 5 (checkout-orchestrator) | Risk 1 (the saga itself). |
| All phases | Risk 5 (scope creep / abandonment). |

Risks 1–4 + 6–7 are technical and discoverable through tests. Risk 5 is behavioral and only mitigated by discipline.

---

## See also

- [03-build-plan.md](./03-build-plan.md) — phased build with acceptance criteria
- [04-testing-strategy.md](./04-testing-strategy.md) — chaos test inventory
- [adr/0003-saga-its-own-service.md](./adr/0003-saga-its-own-service.md)
- [adr/0008-clean-slate-greenfield.md](./adr/0008-clean-slate-greenfield.md)
