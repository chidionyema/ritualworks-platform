# Getting Started

Zero-to-running in 5 minutes. This guide gets you a fully working local dev environment.

## Prerequisites

| Tool | Version | Install |
|---|---|---|
| .NET SDK | 9.0+ | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/9.0) |
| Docker Desktop | 4.x+ | [docker.com](https://www.docker.com/products/docker-desktop/) |
| .NET Aspire workload | latest | `dotnet workload install aspire` |

Verify:

```bash
dotnet --version    # 9.0.x
docker --version    # 24+
```

## 1. Clone

```bash
git clone https://github.com/chidionyema/ritualworks-platform.git
cd ritualworks-platform
```

## 2. Start the platform (Aspire)

```bash
cd deploy/aspire
dotnet run
```

First run pulls ~2 GB of Docker images (Postgres, Redis, RabbitMQ, Kafka, Elasticsearch, Vault, etc.). Subsequent runs reuse them instantly via persistent containers.

The **Aspire Dashboard** opens at `http://localhost:15888` with live logs, traces, and health for every service.

## 3. Verify it works

```bash
# Health check
curl http://localhost:5050/health

# Browse the catalogue (no auth required)
curl http://localhost:5050/api/catalog/products
```

## Key endpoints

| What | URL |
|---|---|
| BFF Web (all API traffic) | http://localhost:5050 |
| Aspire Dashboard | http://localhost:15888 |
| Vault (dev token: `dev-root-token`) | http://localhost:8200 |
| RabbitMQ Management | http://localhost:15672 |
| Pact Broker | http://localhost:9292 |

## Alternative: Docker Compose

If you prefer not to use Aspire:

```bash
docker compose -f deploy/compose/docker-compose.yml up -d
dotnet run --project src/BffWeb/BffWeb.Api
```

## Building a single service (fast)

Full solution build takes ~90s. Use solution filters for ~15s builds:

```bash
dotnet build filters/Payments.slnf
dotnet test filters/Payments.slnf
```

Available filters: Audit, BffWeb, Catalog, CheckoutOrchestrator, Content, Identity, Location, Merchant, Notifications, Orders, Payments, Payouts, Pricing, Privacy, Scheduler, Search, Webhooks.

## Running tests

```bash
# Unit tests only (no Docker needed)
dotnet test RitualworksPlatform.sln --filter "Category=Unit"

# Single service integration tests (Docker required)
dotnet test tests/Catalog/Catalog.Integration/

# Architecture guards
dotnet test tests/Platform.ArchitecturalGuards/
```

See [README.md Section 6](../README.md#6-testing) for the full test pyramid.

## Troubleshooting

| Problem | Fix |
|---|---|
| Port 5050 already in use | Stop other services on that port, or set `ASPNETCORE_URLS` in `deploy/aspire/` |
| Docker containers not starting | Ensure Docker Desktop is running; check `docker ps` for conflicts |
| Aspire dashboard blank | Wait ~60s for all health checks to pass on first run |
| "Vault sealed" errors | Aspire starts Vault in dev mode automatically; if using Compose, run `./scripts/seed-vault-dev.sh` |
| EF migration errors | Each service auto-migrates on startup in Development mode |

## Next steps

- [docs/INDEX.md](INDEX.md) — documentation navigation by role
- [CONTRIBUTING.md](../CONTRIBUTING.md) — how to contribute
- [docs/BACKLOG.md](BACKLOG.md) — production readiness backlog
- [docs/microservices-migration/](microservices-migration/) — architecture decisions (ADRs)
