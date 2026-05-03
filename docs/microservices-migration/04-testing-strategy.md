# 04 — Testing Strategy

## The Reference Monolith's Test Suite

The existing modular monolith (in its original repo) has **~158 test files** across 7 test projects. Because we're building greenfield ([ADR-0008](./adr/0008-clean-slate-greenfield.md)) with the monolith as a reference ([ADR-0009](./adr/0009-monolith-as-reference-not-source.md)), this inventory is a **coverage checklist** for what tests to write per service in the new repo — not a migration manifest. New tests are written against the new code, taking the monolith's tests as reference for *what cases need coverage*.

| Project | .cs files | Use as reference for |
|---|---:|---|
| `haworks.Tests.Unit` | 84 | What handler/domain/validator/controller tests each new service should have |
| `haworks.Tests.integration` | 65 | What full-stack scenarios to cover with Testcontainers per service |
| `haworks.Tests.Smoke` | 3 | Environment-agnostic smoke pattern (`SMOKE_TARGET_URL` env override) |
| `haworks.Tests.Contract` | 1 | The Pact v5 wiring template (`CatalogToPaymentsContractTests.cs`) |
| `haworks.Tests.Architecture` | 1 | The NetArchTest boundary check pattern |
| `haworks.Tests.Performance` | 2 | Benchmark scaffolding |
| `haworks.Tests.E2E` | 2 | Playwright journey patterns |

**Subdirectories matter** because they map to which new service the test corresponds to:

```
tests/haworks.Tests.Unit/
├── Architecture/    → reference for each-service Architecture project
├── Commands/        → reference per bounded context (cribbed into <svc>.Unit)
├── Consumers/       → reference per bounded context
├── Controllers/     → reference per bounded context
├── Domain/          → reference per bounded context
├── Payments/        → reference for Payments.Unit
├── Queries/         → reference per bounded context
└── Validators/      → reference per bounded context
```

```
tests/haworks.Tests.integration/
├── Chaos/           → reference for per-service chaos folders + cross-service E2E chaos
├── Consumers/       → reference per service
├── Controllers/     → reference per service
├── Observability/   → reference for tests/Observability (cross-cutting in BuildingBlocks)
├── Outbox/          → critical reference: PaymentAndCatalogOutboxWiringProbe etc.
├── Regression/      → audit references
├── Saga/            → reference for CheckoutOrchestrator.Integration
└── Vault/           → reference for BuildingBlocks.Vault tests
```

The new repo never depends on the monolith's tests — they're inspiration. New tests live in the new repo, in the new structure, against the new code.

---

## Per-Service Test Layout (New Repo)

Each service folder gets four test projects:

| Project | What | Run on |
|---|---|---|
| `<Service>.Unit` | Handler tests, domain invariants, validator tests. Fully mocked. | Every PR |
| `<Service>.Integration` | Testcontainers (only the containers this service needs). Full-stack endpoint + consumer tests. | Every PR |
| `<Service>.Contract` | Pact producer + consumer tests. Publishes to broker. | Every PR |
| `<Service>.Architecture` | NetArchTest boundary check — "may only reference Contracts + BuildingBlocks." | Every PR |

Per-service Testcontainer subset (no service runs containers it doesn't need):

| Service | Containers required |
|---|---|
| identity-svc | Postgres, Redis |
| catalog-svc | Postgres, Redis, RabbitMQ |
| orders-svc | Postgres, RabbitMQ |
| payments-svc | Postgres, RabbitMQ |
| content-svc | Postgres, MinIO, ClamAV |
| checkout-orchestrator-svc | Postgres, RabbitMQ |
| bff-web | None (uses WireMock for downstream services) |

Catalog drops MinIO and starts ~30 s faster than today's monolith integration suite. Payments doesn't need Redis. Content is the only service that needs MinIO + ClamAV.

---

## Shared Test Infrastructure: `Haworks.Testing.Containers` NuGet

The monolith's `tests/haworks.Tests.integration/_Infrastructure/Docker/TestcontainersInfrastructure.cs` is the **most-cribbed test file** going forward. To prevent drift across 7 services, it's published as a NuGet package from `src/BuildingBlocks.Testing/`:

```csharp
// In every service's *.Integration test project
public class CatalogIntegrationFixture : Haworks.Testing.Containers.TestcontainersFixture
{
    protected override Containers Required => Containers.Postgres | Containers.Redis;
}
```

The base fixture:
- Pulls images in parallel via `docker pull` for cold-start optimization (proven pattern from monolith's `PrePullImagesAsync`).
- Pinned image tags in **one place** — Renovate updates them.
- Implements `IAsyncLifetime` for xUnit.
- Built-in **per-fixture topology prefix** (current `PrefixedTestEntityNameFormatter` pattern) so parallel-test isolation across services in CI just works.

**Pinned image tags** (single source of truth):
- `postgres:16-alpine`
- `redis:7-alpine`
- `rabbitmq:3.13-management-alpine`
- `minio/minio:RELEASE.2024-...`
- `clamav/clamav:1.4`
- `pactfoundation/pact-broker:latest`
- `hashicorp/vault:1.15`

---

## Aspire and Tests

Aspire is for *running* the system locally, not for *testing* it. Integration tests own their containers via Testcontainers; Aspire is for `dotnet run --project deploy/aspire`.

The cross-system smoke tests in `tests/E2E/Smoke/` **do** want the full stack running:

```bash
# Local
dotnet run --project deploy/aspire &
./scripts/wait-for-services.sh   # health-check loop, no arbitrary delay
dotnet test tests/E2E/Smoke --filter Category=CrossSystemSmoke

# CI (kind)
make k8s-up
./scripts/k8s-smoke.sh
make k8s-down
```

`scripts/local-deploy-smoke.sh` is the canonical "local deploy works" acceptance test. Required PR check on every change to `src/`, `deploy/`, or `infra/`.

---

## Contract Tests as the Source of Truth

The Pact broker is the **source of truth for cross-service contracts**.

### Producer/consumer assignment per cross-service event

| Event | Producer | Consumers |
|---|---|---|
| `OrderCreatedEvent` | orders-svc | checkout-orchestrator |
| `StockReservedEvent` | catalog-svc | checkout-orchestrator, payments-svc |
| `StockReservationFailedEvent` | catalog-svc | checkout-orchestrator, orders-svc |
| `StockReleaseRequestedEvent` | checkout-orchestrator | catalog-svc |
| `StockReleasedEvent` | catalog-svc | checkout-orchestrator |
| `PaymentSessionRequestedEvent` | checkout-orchestrator | payments-svc |
| `PaymentSessionCreatedEvent` | payments-svc | checkout-orchestrator, orders-svc |
| `PaymentSessionFailedEvent` | payments-svc | checkout-orchestrator |
| `PaymentCompletedEvent` | payments-svc | orders-svc, checkout-orchestrator, fulfillment, analytics |
| `PaymentAmountMismatchEvent` | payments-svc | orders-svc, alerting |
| `PaymentVerifiedEvent` | payments-svc | orders-svc |
| `PaymentWebhookValidatedEvent` | payments-svc | (internal — payments-svc consumes its own webhook handoff) |
| `OrderCompletedEvent` | orders-svc | notifications, fulfillment, analytics |
| `OrderAbandonedEvent` | orders-svc | notifications |
| `CheckoutInitiatedEvent` | bff-web/orders-svc | catalog-svc, checkout-orchestrator |
| `UserProfileChangedEvent` | identity-svc | all services that maintain user snapshots |

**Sync RPC contracts** (gRPC) get HTTP-flavored Pacts:
- identity-svc: `IntrospectToken`, `GetUser`
- catalog-svc: `ReserveStock`, `ReleaseStock`, `GetProduct`

### Workflow

1. PR opens against svc-A that changes a published event's shape.
2. svc-A's CI runs unit + integration + Pact provider verification against existing broker contracts. **If any consumer's contract is violated, PR is blocked.**
3. Developer either:
   - Reverts the breaking change → PR proceeds.
   - Bumps to v2 of the event (additive within a major; new exchange for breaking) → consumers can migrate at their own pace.
4. svc-A's CI publishes the new pact (additive) or new contract (v2) to broker tagged `branch:<name>`, `version:<sha>`.
5. `pact-broker can-i-deploy --pacticipant svc-A --version <sha> --to-environment production` returns `true` → PR can merge.
6. Webhook fires on `contract_content_changed` → triggers downstream consumer pipelines to verify against the new producer schema.

### CI gating

The `can-i-deploy` check is a **required GitHub status** on every PR to a service folder. No exceptions, no overrides without a `breaking-change-approved` label that requires an architect review.

### Local development

Pact broker runs in Aspire (`AddContainer("pact-broker", ...)`) so devs can publish/verify without touching CI. Tests target `http://localhost:9292` in dev, `${PACT_BROKER_URL}` in CI.

---

## Chaos Testing

A first-class concern. Three layers:

### Service-internal chaos (per service)

Each service has a `<Service>.Integration/Chaos/` folder. Tests pause the service's *dependencies* (DB, Redis, etc.) and assert graceful degradation per the resilience patterns from the reference monolith (circuit breakers, fallbacks, bulkheads).

### Cross-service chaos (in `tests/E2E/Chaos/`)

The **headline chaos test** for the portfolio. Lives in `tests/E2E/Chaos/SagaCompensationChaosTests.cs`:

```
1. Start a checkout via bff-web
2. Wait for OrderCreated, StockReserved (poll, no Task.Delay)
3. kubectl pause payments-svc pod
4. Wait 90s
5. Assert: saga in CheckoutOrchestrator transitioned to "Compensating"
6. Assert: catalog-svc received StockReleaseRequested
7. Assert: stock count matches pre-checkout (released)
8. Assert: orders-svc Order is "Abandoned"
9. Assert: notifications-svc sent abandonment email (mock SMTP)
10. kubectl unpause payments-svc
11. Run a fresh checkout end-to-end → assert green
```

**Recorded as a 2-minute Loom demo for the README.** This is the crown jewel artifact.

### Network chaos (Phase 8 stretch goal)

Use `chaos-mesh` or `litmus` to inject network partitions between services in kind. Assert the saga still recovers. Optional but high-impact for portfolio.

---

## Local Deploy Parity

The constraint: **`dotnet run --project deploy/aspire` and `make k8s-up` both work and produce the same observable behavior.** Acceptance test:

```bash
#!/bin/bash
# scripts/local-deploy-smoke.sh

# Start everything via Aspire
dotnet run --project deploy/aspire &
APP_PID=$!

# Wait for all services healthy (poll, max 90s)
for svc in identity catalog orders payments content checkout-orchestrator bff-web; do
  ./scripts/wait-for-health.sh "http://localhost:$(get-port $svc)/health" 90
done

# Exercise critical paths via cross-system smoke
SMOKE_TARGET_URL=http://localhost:5001 \
  dotnet test tests/E2E/Smoke --filter Category=CrossSystemSmoke

# Cleanup
kill $APP_PID
```

**Required PR check** on every change to `src/`, `deploy/`, `infra/`. CI runs this against ephemeral kind cluster (faster path: `make k8s-up`, then this script with `SMOKE_TARGET_URL` pointing at kind ingress).

If this script can't compose, no merge.

---

## Smoke Tests — Two Layers

Inherited environment-agnostic pattern (`SMOKE_TARGET_URL=http://...` env override) from the monolith reference:

1. **Per-service smoke** — each service's CI runs smoke against its own deployed URL post-deploy. Just `/health` and one happy-path endpoint.
2. **Cross-system smoke** — exercises register → login → browse → checkout → webhook → order completed through bff-web ingress. Runs on every PR against ephemeral kind cluster. Lives in `tests/E2E/Smoke/`.

---

## NO Task.Delay Mandate

Inherited from the monolith's `CLAUDE.md`. No hardcoded `Task.Delay` in tests. The monolith's `tests/haworks.Tests.integration/_Infrastructure/TestWait.cs` (`TestWait.Until` for positive polling, `TestWait.NotHappens` for negative observation window) is cribbed into the new repo's `Haworks.Testing` NuGet so every service uses the same primitive. **All chaos and saga tests use `TestWait`** — observability of timing is part of the test contract.

---

## Test-Health Gating

| Signal | Action |
|---|---|
| Single flake | Re-run job |
| Two flakes in 24 h on same test | Halt new merges to that service folder; root-cause within 4 h |
| Pact `can-i-deploy` red | PR blocked. Fix the schema or revert. |
| `scripts/local-deploy-smoke.sh` red on `main` | Halt all merges until green. The local-deploy guarantee is non-negotiable. |

---

## See also

- [03-build-plan.md](./03-build-plan.md) — phase-by-phase test posture
- [02-platform.md](./02-platform.md) — CI workflow, kind smoke
- [05-risks.md](./05-risks.md) — test-infra drift risk
