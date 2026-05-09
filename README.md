# Ritualworks Platform

> **Production-shape distributed system on .NET 9.** Seven bounded-context microservices, choreography-based saga, transactional outbox per service, end-to-end chaos test that proves compensation works against real Postgres + RabbitMQ + the saga state machine.

[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Aspire](https://img.shields.io/badge/Aspire-9.0-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/aspire/)
[![MassTransit](https://img.shields.io/badge/MassTransit-8.3-FF6F00)](https://masstransit.io/)
[![Postgres](https://img.shields.io/badge/Postgres-16-336791?logo=postgresql&logoColor=white)](https://www.postgresql.org/)
[![RabbitMQ](https://img.shields.io/badge/RabbitMQ-3.13-FF6600?logo=rabbitmq&logoColor=white)](https://www.rabbitmq.com/)
[![Vault](https://img.shields.io/badge/Vault-1.17-000000?logo=vault&logoColor=white)](https://www.vaultproject.io/)
[![OpenTelemetry](https://img.shields.io/badge/OpenTelemetry-1.9-425CC7?logo=opentelemetry&logoColor=white)](https://opentelemetry.io/)
[![Pact](https://img.shields.io/badge/Pact-CDC-1F6FEB)](https://docs.pact.io/)

---

## TL;DR

| | |
|---|---|
| **Services** | catalog · orders · payments · identity · content · bff-web · checkout-orchestrator |
| **Per-service guarantees** | own database, own outbox, own consumer endpoints, own migrations |
| **Cross-service comms** | MassTransit + RabbitMQ; events typed in `src/Contracts/`; producer-side Pact contracts |
| **Saga** | MassTransit state machine (`CheckoutOrchestrator`) — choreographs `OrderCreated → StockReserved → PaymentSession → PaymentCompleted` with compensation on any failure |
| **Test pyramid** | 240+ tests: unit (per-context), architecture (NetArchTest), contract (Pact), integration (Testcontainers), chaos (saga compensation against real infra) |
| **Local dev** | `dotnet run --project deploy/aspire` — Aspire spins up Postgres × N, RabbitMQ, Vault, Minio, all 7 services with wired connection strings |
| **Production-shape** | `make k8s-up` — kind cluster, Helm charts, ArgoCD App-of-Apps |

---

## Architecture at a Glance

```
                    ┌──────────────────────────────────────────┐
                    │           bff-web (BFF)                  │
                    │  Aggregates downstream services for SPA  │
                    └────────────────────┬─────────────────────┘
                                         │
       ┌─────────────────────────────────┼────────────────────────┐
       │                                 │                         │
┌──────▼──────┐  ┌──────────┐  ┌────────▼────────┐  ┌────────────┐
│   catalog   │  │ identity │  │     orders      │  │  payments  │
│  Products,  │  │  JWT,    │  │  Cart, Order,   │  │  Stripe,   │
│  Stock res. │  │  refresh │  │  guest checkout │  │  PayPal    │
└──────┬──────┘  └──────────┘  └────────┬────────┘  └─────┬──────┘
       │                                 │                  │
       │            ┌────────────────────▼──────────────────▼──┐
       │            │          checkout-orchestrator           │
       │            │  MassTransit saga (CheckoutSagaState)    │
       │            │  drives the OrderCreated → StockReserved │
       └────────────►  → PaymentSession → PaymentCompleted     │
                    │  workflow + compensation                 │
                    └──────────────────────────────────────────┘

   ▲ event-driven, typed contracts in src/Contracts, RabbitMQ + EF outbox per service ▲
```

Each box owns its own:
- Database schema + EF Core migrations
- Outbox table + MassTransit `EntityFrameworkOutboxBusOutbox`
- Consumer endpoints (no shared `ConsumeContext` across boundaries)
- Pact contract for the events it publishes

There is no shared kernel — `BuildingBlocks` provides cross-cutting infra (resilience, vault, observability, current-user) but no domain types.

---

## The Crown Jewel: Saga Compensation Chaos Test

[`tests/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs`](./tests/CheckoutOrchestrator.Integration/SagaCompensationChaosTests.cs)

A single test stands up:
- Two Testcontainers Postgres instances (one for `checkout`, one for `catalog`)
- An in-memory MassTransit harness wiring **the actual saga state machine + the actual catalog `StockReleaseRequestedConsumer`**
- Real EF outbox dispatch on both sides

It then drives a checkout, deliberately fails the payment session step, and asserts:
1. The saga transitioned `Pending → AwaitingStock → AwaitingPayment → Compensating → Failed`
2. The catalog reservation row was decremented back to original stock
3. Both outboxes flushed their compensation messages

This is the difference between "we have a saga" and "we have a saga that actually compensates under failure."

---

## What Each Service Owns

| Service | Public surface | Database | Headline events |
|---------|----------------|----------|-----------------|
| **catalog** | Products, categories, stock | `catalog` | `StockReserved`, `StockReleased`, `StockReservationFailed` |
| **identity** | Login, refresh, profile, JWT issuance + revocation | `identity` | `UserRegistered`, `UserProfileUpdated` |
| **orders** | Cart, order placement, guest checkout | `orders` | `OrderCreated`, `OrderCompleted`, `OrderCancelled` |
| **payments** | Stripe + PayPal sessions, webhook ingest, idempotent dedup | `payments` | `PaymentSessionRequested`, `PaymentCompleted`, `PaymentSessionFailed`, `PaymentAmountMismatch` |
| **content** | File upload, MinIO storage, image variants | `content` | (chunked upload pipeline pending) |
| **bff-web** | SPA aggregator, no business logic | n/a | n/a |
| **checkout-orchestrator** | Saga state machine | `checkout` | `CheckoutCompleted`, `CheckoutFailed`, `StockReleaseRequested` |

---

## Test Inventory

```
Unit + Architecture + Contract:    218 tests, all green
Integration (Testcontainers):
  catalog                            7/7
  orders                             9/9
  payments                           7/7  (idempotency proven via DB-level WebhookEvent unique index)
  identity                          14/14
  bff-web                            3/3
  checkout-orchestrator              7/7  (includes saga compensation chaos test)
  content                            2/2  (10 skipped — chunked upload + Minio wiring incomplete)
```

Run the whole thing:
```bash
dotnet test
```

Or one suite:
```bash
dotnet test tests/CheckoutOrchestrator.Integration/CheckoutOrchestrator.Integration.csproj
```

---

## Run Locally

```bash
# One-time setup
dotnet workload install aspire
dotnet dev-certs https --trust

# Daily dev — Aspire orchestrates Postgres × N, RabbitMQ, Vault, Minio, all services
dotnet run --project deploy/aspire

# Seed Vault dev secrets (first run only)
./scripts/seed-vault-dev.sh
```

Use the helper script `./scripts/aspire-up.sh` only when a previous run left orphan service processes around, ports are stuck, or you're running in CI — it pre-cleans orphan `dotnet`/AppHost/dcpctrl processes, does a Docker preflight, builds the AppHost, and runs a watchdog that gates on `http://localhost:5050/health` and tails per-service logs into `./logs/`. For everyday work, the plain `dotnet run` above is what you want.

Aspire dashboard URL appears in console — gives aggregated logs, OTel traces, and per-service health.

---

## Production-Shape

```bash
make k8s-up        # kind cluster + Helm charts + ArgoCD App-of-Apps
make k8s-status    # ArgoCD sync state
make k8s-down      # tear down
```

Deploy artifacts live under [`deploy/`](./deploy/) and [`infra/`](./infra/).

---

## Key Engineering Decisions

| Decision | ADR |
|----------|-----|
| Strict monorepo | [0001](./docs/microservices-migration/adr/0001-strict-monorepo.md) |
| Aspire local + kind prod | [0002](./docs/microservices-migration/adr/0002-aspire-local-kind-prod.md) |
| Saga is its own service | [0003](./docs/microservices-migration/adr/0003-saga-its-own-service.md) |
| Database per service | [0004](./docs/microservices-migration/adr/0004-database-per-service.md) |
| JWT RS256 + JWKS | [0005](./docs/microservices-migration/adr/0005-jwt-rs256-jwks.md) |
| Self-hosted Pact broker | [0006](./docs/microservices-migration/adr/0006-self-hosted-pact-broker.md) |
| Strangler-fig migration from monolith | [0007](./docs/microservices-migration/adr/0007-strangler-fig-migration.md) |
| Greenfield platform repo | [0008](./docs/microservices-migration/adr/0008-clean-slate-greenfield.md) |
| Monolith is reference, not source | [0009](./docs/microservices-migration/adr/0009-monolith-as-reference-not-source.md) |

Architecture deep dive: [`docs/microservices-migration/01-architecture.md`](./docs/microservices-migration/01-architecture.md)

---

## Repository Layout

```
src/
├── BuildingBlocks/           Cross-cutting infrastructure (no domain types)
│   ├── Resilience/             Polly policies, circuit breakers, bulkheads
│   ├── Vault/                  HashiCorp Vault dynamic credentials + AppRole
│   ├── Persistence/            EF outbox, dynamic-creds connection interceptor
│   ├── Messaging/              MassTransit + outbox + bounded-context endpoint conventions
│   ├── Telemetry/              OpenTelemetry pipelines
│   └── ...
├── BuildingBlocks.Testing/   Shared test scheme + Testcontainers module init
├── Contracts/                Typed event payloads (one folder per publishing context)
├── Catalog/                  catalog-svc (Domain, Application, Infrastructure, Api)
├── Identity/                 identity-svc
├── Orders/                   orders-svc
├── Payments/                 payments-svc
├── Content/                  content-svc
├── BffWeb/                   bff-web
└── CheckoutOrchestrator/     checkout-orchestrator (saga)

tests/
├── *.Unit/                   Per-context unit tests (handlers, validators, domain)
├── *.Architecture/           NetArchTest enforcement (no cross-context refs, naming, etc.)
├── *.Contract/               Pact provider + consumer tests
└── *.Integration/            Testcontainers Postgres + MassTransit harness

deploy/
├── aspire/                   Local AppHost for Aspire dashboard
└── helm/                     Helm charts per service + umbrella

infra/
└── argocd/                   App-of-Apps + per-service ArgoCD Applications
```

---

## License

MIT
