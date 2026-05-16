# Haworks: Distributed Microservices Platform

> **Portfolio case study.** A working production-shape distributed system showcasing event-driven microservices, transactional outbox/inbox, MassTransit saga state machines with compensation, contract testing with Pact, GitOps deploys via ArgoCD, dynamic Vault-issued database credentials, and full distributed tracing — all runnable on a laptop in one command.

---

## What This Is

This repository implements a .NET 9 Clean Architecture distributed system as 7 independently-deployable microservices: identity, catalog, orders, payments, content, checkout-orchestrator (saga), and bff-web.

Built clean from scratch in 8 weeks, the system is delivered as a **strict monorepo** with physically-enforced service boundaries, deployed via Helm + ArgoCD onto a local Kubernetes cluster (kind), with the same manifests retargetable at AWS EKS by changing one values file.

A prior modular-monolith implementation of the same domain exists (in a separate repo) as a code-cribbing reference — the new system was built greenfield, informed by what the monolith already knew about the domain. See [ADR-0008](./adr/0008-clean-slate-greenfield.md) and [ADR-0009](./adr/0009-monolith-as-reference-not-source.md) for why this approach over a strangler-fig migration.

## Why This Is Interesting

| What you'll see | Where to look |
|---|---|
| **Per-service transactional outbox** with a generic `BoundedContextConsumerDefinition<T,TDb>` wiring shape so each service's outbox + inbox + business state commits in one local transaction | [01-architecture.md](./01-architecture.md#per-service-outboxinbox) |
| **MassTransit saga state machine** with explicit compensation paths (release stock, mark order abandoned, notify customer) — its own service, its own DB, queryable for ops | [01-architecture.md](./01-architecture.md#the-saga-checkoutorchestrator) |
| **Pact contract tests as a hard PR gate** — `can-i-deploy` blocks merges that break a known consumer | [04-testing-strategy.md](./04-testing-strategy.md#contract-tests-as-the-source-of-truth) |
| **Greenfield 8-week build plan** with vertical-slice-first to establish the canonical service template | [03-build-plan.md](./03-build-plan.md) |
| **Vault Kubernetes auth** for production, AppRole for dev — same policy HCL files for both | [02-platform.md](./02-platform.md#secrets-vault-topology) |
| **Asymmetric JWT (RS256) + JWKS endpoint** — no shared secret to rotate across services | [02-platform.md](./02-platform.md#authentication--jwt) |
| **Database-per-service** in a shared cluster, with services treating each other's `UserId` as opaque foreign keys | [01-architecture.md](./01-architecture.md#data-strategy) |
| **OpenTelemetry through the outbox** — `traceparent` propagation across async hops, verified by synthetic | [02-platform.md](./02-platform.md#observability) |
| **Distributed saga chaos demo** — `make demo-saga-failure` kills a service mid-checkout and proves compensation works | [04-testing-strategy.md](./04-testing-strategy.md#chaos-testing) |

## Run Locally

```bash
# Daily dev (Aspire — one process tree, sub-30s startup, live reload)
dotnet run --project deploy/aspire

# Production-shape (kind cluster + ArgoCD + Vault K8s auth + Helm)
make k8s-up
```

Both paths surface the same dashboards (Aspire dashboard for dev; Grafana + ArgoCD UI for k8s).

## Repository Layout

```
haworks-platform/
├── src/
│   ├── Identity/                ← microservice (own .csproj, own .sln, own DbContext)
│   ├── Catalog/
│   ├── Orders/
│   ├── Payments/
│   ├── Content/
│   ├── CheckoutOrchestrator/    ← the saga's own service
│   ├── BffWeb/                  ← public HTTP edge (CSRF, controller mapping, SignalR)
│   ├── Contracts/               ← shared NuGet: integration events + .proto
│   └── BuildingBlocks/          ← Result<T>, MediatR behaviors, outbox base, Vault interceptor
├── tests/
│   ├── <Service>.Unit/
│   ├── <Service>.Integration/   ← Testcontainers per service
│   ├── <Service>.Contract/      ← Pact producer & consumer tests
│   ├── <Service>.Architecture/  ← NetArchTest boundary enforcement
│   └── E2E/                     ← Playwright end-to-end
├── deploy/
│   ├── aspire/                  ← local-dev AppHost
│   ├── helm/<svc>/              ← per-service Helm chart
│   ├── argocd/                  ← App-of-Apps + per-svc Application
│   └── vault/                   ← policies, k8s-auth config
├── infra/
│   ├── pact-broker/             ← self-hosted broker (docker-compose)
│   └── observability/           ← otel-collector, tempo, loki, prometheus configs
├── docs/
│   ├── microservices-migration/ ← this directory (architecture + build plan + ADRs)
│   └── case-study/              ← phase-by-phase write-up (the recruiter pitch)
└── README.md
```

## Documentation Index

| Document | Purpose |
|---|---|
| [01-architecture.md](./01-architecture.md) | Service map, repo boundaries, data ownership, communication patterns, the saga |
| [02-platform.md](./02-platform.md) | Local dev, CI/CD, Kubernetes deploy, observability, secrets, Pact broker |
| [03-build-plan.md](./03-build-plan.md) | 8-week greenfield build plan with per-phase acceptance criteria |
| [04-testing-strategy.md](./04-testing-strategy.md) | Test pyramid, contract testing workflow, chaos testing, local-deploy parity |
| [05-risks.md](./05-risks.md) | Top 7 risks ranked, with mitigations and tripwires |
| [adr/](./adr/) | Architecture Decision Records — the "why" for every non-obvious choice |

## Status

Build plan in [03-build-plan.md](./03-build-plan.md) tracks weekly progress. The reference monolith stays in its original repo, untouched.

## Tech Stack

- **.NET 9** — Aspire, ASP.NET Core, EF Core 9
- **MassTransit 8** — RabbitMQ transport, EF Core outbox, saga state machines
- **PostgreSQL 16** — one database per service in a shared cluster
- **HashiCorp Vault** — AppRole (dev) + Kubernetes auth (prod), dynamic DB credentials
- **YARP** — composition-layer reverse proxy inside `bff-web`
- **gRPC + REST** — gRPC for service-to-service, REST for browser/external
- **Pact (PactNet v5)** — message + HTTP contract tests, self-hosted broker
- **OpenTelemetry** — traces (Tempo), logs (Loki), metrics (Prometheus), Grafana UI
- **Kubernetes** — kind locally, EKS-ready manifests via Helm + ArgoCD
- **Playwright** — end-to-end browser tests
