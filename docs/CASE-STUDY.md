# Case Study: Haworks Monolith → 7-Service Microservices Platform

**Stack:** .NET 9, Aspire, MassTransit + RabbitMQ, Postgres 16, Vault, OpenTelemetry, Helm, ArgoCD, kind
**Outcome:** A production-shape distributed system with 240+ tests, including a chaos test that proves saga compensation works end-to-end against real infrastructure.

---

## The Problem

A working .NET monolith — `haworks` — had grown into seven loosely-bounded contexts (catalog, orders, payments, identity, content, checkout, BFF). Cross-context calls were synchronous DB joins; deploys were all-or-nothing; one slow checkout request tied up worker threads for the entire app. The codebase had defensive patterns (idempotency keys, optimistic concurrency, manual compensation) that were *fighting the procedural orchestration* — proof that the system wanted to be event-driven but was wired against it. See [`event-integration-rationale.md`](../.claude/rules/event-integration-rationale.md).

The goal: split into independently-deployable services without losing the consistency guarantees the monolith leaned on.

---

## The Hard Decisions

### Database-per-service from day one ([ADR-0004](./microservices-migration/adr/0004-database-per-service.md))
No shared schema, no shared DbContext. Each service owns its own Postgres database. Cross-context queries go through events, not joins. This was the **single most important call** — every other decision flows from "you cannot just `JOIN` the other service's table."

### Saga as its own service ([ADR-0003](./microservices-migration/adr/0003-saga-its-own-service.md))
The checkout flow involves orders, catalog stock, and payments. The temptation was to put orchestration inside `orders-svc`. Instead, `checkout-orchestrator` is a third-party MassTransit state machine that consumes events from all three and drives the workflow. It has no business logic — only state transitions and compensation. This kept the three core services free of cross-context awareness.

### Transactional outbox per service, not a shared event bus
Every service writes outbox rows in the same DB transaction as its state changes. MassTransit's `EntityFrameworkOutboxBusOutbox` flushes them. This is the "exactly one publish per state transition" guarantee we couldn't have gotten by publishing directly to RabbitMQ from a handler.

### Greenfield platform repo ([ADR-0008](./microservices-migration/adr/0008-clean-slate-greenfield.md), [ADR-0009](./microservices-migration/adr/0009-monolith-as-reference-not-source.md))
The monolith stayed at `haworks/`. The platform built up at `haworks-platform/` from an empty `Foundation` phase. The monolith is read-only reference: when a behavior is unclear, we read its implementation, but we do not import its code. This avoided dragging in the procedural patterns the migration was meant to escape.

---

## What Worked

### Aspire as the local-dev backbone ([ADR-0002](./microservices-migration/adr/0002-aspire-local-kind-prod.md))
A single `dotnet run --project deploy/aspire` spins up Postgres × N (one per service), RabbitMQ, Vault (dev mode), Minio, and all seven services with connection strings auto-injected. The dashboard gives aggregated logs and OTel traces. Onboarding went from "follow this 14-step Docker Compose ritual" to "install one workload, run one command."

### Choreography + saga, not pure choreography
Initial discussions favored pure event choreography with no orchestrator. We tried it. The compensation paths were impossible to reason about — every consumer had to know about every other service's failure modes. Adding the saga as an orchestrator that *coordinates* without *containing business logic* was the right middle ground.

### Pact contracts for cross-service events ([ADR-0006](./microservices-migration/adr/0006-self-hosted-pact-broker.md))
Every service-to-service event has a Pact contract verified by the consuming side and the producing side independently. When `payments` adds a field, `orders` knows immediately. This catches the "we shipped the producer, forgot to deploy the consumer" class of bugs at PR time.

---

## What Went Wrong

### Testcontainers + Docker Desktop on macOS
Tests blocked repo-wide on day one of integration work. Three layers of pain:
1. Testcontainers' `MatchImage` regex hit catastrophic backtracking on a benign image tag → fixed by raising the default regex timeout in a per-assembly `[ModuleInitializer]`.
2. Docker Desktop's socket path moved (`/var/run/docker.sock` → `~/.docker/run/docker.sock`) → fixed by setting `DOCKER_HOST` in the same module init.
3. The Ryuk reaper container couldn't bind-mount the new socket path → fixed by setting `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` to the legacy path.

The fix lives in [`src/BuildingBlocks.Testing/TestModuleInitializer.cs`](../src/BuildingBlocks.Testing/TestModuleInitializer.cs) and is `<Compile Include>`-linked into every integration test project.

### In-memory test transport vs. EF outbox
The Payments idempotency test was flaky: replaying the same Stripe webhook 3× sometimes produced 3 `PaymentCompleted` publishes instead of 1. The cause: the in-memory MassTransit transport publishes synchronously, while the production EF outbox captures publishes inside the same DB transaction as the dedup row. The fix wasn't to change production; it was to **move the idempotency assertion to the DB row count** (the production-correct guarantee) instead of the publish count (the transport-specific limitation). The comments in [`PaymentWebhookValidatedConsumer.cs`](../src/Payments/Payments.Application/Consumers/PaymentWebhookValidatedConsumer.cs) document the trade-off.

### The chaos test that almost wasn't
The headline `SagaCompensationChaosTests` initially used `harness.Consumed.Any<T>()` to wait for events. This returned `true` microseconds before the downstream publish actually landed in `harness.Published`, causing race-condition failures. Replaced with `PollUntilAsync` that polls the actual end-state condition (saga is `Failed`, stock count is restored). The chaos test then went from flaky-when-CI-was-loaded to deterministic.

---

## What I'd Do Differently

- **Set up CI on day one.** I left it for last. Several issues only surfaced when running the full suite from scratch, which I didn't do until late.
- **Build content-svc later.** Content was scaffolded in Phase 7 with chunked-upload semantics that turned out to need 3-4 more services I didn't have time for. Those tests are skipped with explicit reasons; in retrospect, content should have been Phase 9 with a narrower scope (single-shot upload only).
- **Adopt OpenAPI-driven contracts earlier.** Pact catches cross-service breakage but doesn't help with REST API drift. An OpenAPI spec per service, validated in CI, would have caught a few BFF-vs-orders mismatches.

---

## Numbers

| | |
|---|---|
| Services | 7 |
| Bounded contexts | 9 (incl. BuildingBlocks + Contracts) |
| Postgres databases | 5 |
| Tests | 240+ |
| Test types | unit · architecture · contract · integration · chaos |
| ADRs | 9 |
| Time from empty repo to v1.0 | 8 phases |

---

## Where to Look Next

- The saga: [`src/CheckoutOrchestrator/CheckoutOrchestrator.Application/`](../src/CheckoutOrchestrator/CheckoutOrchestrator.Application/)
- The chaos test: [`tests/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs`](../tests/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs)
- The outbox pattern in action: [`src/BuildingBlocks/Messaging/`](../src/BuildingBlocks/Messaging/)
- The architecture doc: [`docs/microservices-migration/01-architecture.md`](./microservices-migration/01-architecture.md)
