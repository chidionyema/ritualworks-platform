# Documentation Index

Start here. Find what you need by role.

## New to the project?

1. **[Getting Started](GETTING-STARTED.md)** — zero-to-running in 5 minutes
2. **[README](../README.md)** — platform overview, architecture, services
3. **[Contributing](../CONTRIBUTING.md)** — PR workflow, conventions, common pitfalls

## Building features

| Topic | Location |
|---|---|
| Service layer pattern | [README Section 5](../README.md#5-development) |
| Adding a new service | [CONTRIBUTING.md](../CONTRIBUTING.md#adding-a-new-service) |
| Domain events / contracts | `src/Contracts/` — all events implement `IDomainEvent` |
| Shared building blocks | `src/BuildingBlocks/` — Result monad, outbox, resilience, JWKS |
| Solution filters (fast builds) | `filters/*.slnf` — one per service (~15s vs ~90s) |

## Testing

| Topic | Location |
|---|---|
| Test pyramid overview | [README Section 6](../README.md#6-testing) |
| Testing strategy (detailed) | [microservices-migration/04-testing-strategy.md](microservices-migration/04-testing-strategy.md) |
| Shared Testcontainers | `src/BuildingBlocks.Testing/Containers/` |
| QA hardened strategy | [microservices-migration/06-qa-hardened-strategy.md](microservices-migration/06-qa-hardened-strategy.md) |

## Architecture decisions

| ADR | Topic |
|---|---|
| [0001](microservices-migration/adr/0001-strict-monorepo.md) | Strict monorepo |
| [0002](microservices-migration/adr/0002-aspire-local-kind-prod.md) | Aspire local, Kind prod |
| [0003](microservices-migration/adr/0003-saga-its-own-service.md) | Saga as its own service |
| [0004](microservices-migration/adr/0004-database-per-service.md) | Database per service |
| [0005](microservices-migration/adr/0005-jwt-rs256-jwks.md) | JWT RS256 + JWKS |
| [0006](microservices-migration/adr/0006-self-hosted-pact-broker.md) | Self-hosted Pact broker |
| [0007](microservices-migration/adr/0007-strangler-fig-migration.md) | Strangler fig migration |
| [0008](microservices-migration/adr/0008-clean-slate-greenfield.md) | Clean slate greenfield |
| [0009](microservices-migration/adr/0009-monolith-as-reference-not-source.md) | Monolith as reference |

## Infrastructure and deployment

| Topic | Location |
|---|---|
| Docker Compose | `deploy/compose/docker-compose.yml` |
| Aspire orchestration | `deploy/aspire/Program.cs` |
| Vault credential flow | [architecture/vault-credential-delivery.md](architecture/vault-credential-delivery.md) |
| Fly.io deployment | [README Section 10](../README.md#10-deployment) |
| Multi-instance Aspire | [ASPIRE_MULTI_INSTANCE.md](ASPIRE_MULTI_INSTANCE.md) |
| Search service deploy | [runbooks/search-service-deployment.md](runbooks/search-service-deployment.md) |

## Security and CI/CD

| Topic | Location |
|---|---|
| Automated scanning overview | [AUTOMATED-SCANNING.md](AUTOMATED-SCANNING.md) |
| CI pipeline | [README Section 7](../README.md#7-cicd) |
| Security workflows | [README Section 8](../README.md#8-security-and-quality) |

## Operational runbooks

| Runbook | When to use |
|---|---|
| [Serilog silent swallow](runbooks/serilog-silent-swallow.md) | Logs missing from output |
| [Aspire launchSettings required](runbooks/aspire-launchsettings-required.md) | Aspire fails to start |
| [Aspire orphan services on macOS](runbooks/aspire-orphan-services-on-macos.md) | Orphaned processes after Ctrl+C |
| [Payments integration Docker flake](runbooks/payments-integration-docker-flake.md) | Payments tests flaky in Docker |
| [Observability Fly OTLP secret](runbooks/observability-fly-otlp-secret.md) | Traces not reaching Tempo |

## Project planning

| Topic | Location |
|---|---|
| Production readiness backlog | [BACKLOG.md](BACKLOG.md) |
| Migration build plan | [microservices-migration/03-build-plan.md](microservices-migration/03-build-plan.md) |
| Risk register | [microservices-migration/05-risks.md](microservices-migration/05-risks.md) |
| Platform case study | [CASE-STUDY.md](CASE-STUDY.md) |
| Checkpoint | [CHECKPOINT.md](CHECKPOINT.md) |
