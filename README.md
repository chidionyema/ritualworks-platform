# Ritualworks Platform

Distributed microservices platform — 7 .NET 9 services, MassTransit + RabbitMQ, transactional outbox per service, MassTransit saga state machine for checkout, Pact contract tests, Vault dynamic credentials, OpenTelemetry, Helm + ArgoCD deploys to kind locally and EKS in production.

## Run Locally

```bash
# Daily dev (Aspire)
dotnet run --project deploy/aspire

# Production-shape (kind + ArgoCD + Helm)
make k8s-up
```

## Documentation

The architecture, build plan, ADRs, and risks live under [docs/microservices-migration/](./docs/microservices-migration/README.md).

Quick links:
- [Architecture](./docs/microservices-migration/01-architecture.md)
- [Platform (local dev, CI/CD, K8s, observability, secrets)](./docs/microservices-migration/02-platform.md)
- [Build plan](./docs/microservices-migration/03-build-plan.md)
- [Testing strategy](./docs/microservices-migration/04-testing-strategy.md)
- [Risks](./docs/microservices-migration/05-risks.md)
- [ADRs](./docs/microservices-migration/adr/)

## Status

Phase 0 — Foundation. Repo skeleton in place; no service code yet.
