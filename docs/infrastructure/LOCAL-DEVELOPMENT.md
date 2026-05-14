# Local Development

## Prerequisites

| Tool | Minimum version | Notes |
|------|----------------|-------|
| Docker Desktop (or equivalent) | 24+ | Required by both Compose and Aspire paths |
| .NET SDK | 9.0 | `dotnet --version` must show 9.x |
| Node.js + PowerShell | 20+ / 7+ | Required only for E2E tests (Playwright) |
| jq | any | Required by `scripts/cdc.sh` |

---

## Option A: Docker Compose

The Compose file at `deploy/compose/docker-compose.yml` starts the full infrastructure stack plus every application service. It is the lowest-friction path and does not require any host SDK toolchain beyond Docker.

### Start everything

```bash
cd deploy/compose
docker compose up -d
```

### Port map

| Container | Host port | Purpose |
|-----------|-----------|---------|
| rw-postgres | 5432 | PostgreSQL (all service databases) |
| rw-redis | 6379 | Redis (L2 cache, sessions) |
| rw-rabbitmq | 5672 / 15672 | RabbitMQ AMQP / management UI |
| rw-vault | 8200 | HashiCorp Vault dev server |
| rw-localstack | 4566 | LocalStack S3 emulator |
| rw-clamav | 3310 | ClamAV virus scanner |
| rw-elasticsearch | 9200 | Elasticsearch |
| rw-kafka | 9092 | Kafka (KRaft mode, no ZooKeeper) |
| rw-debezium-connect | 8083 | Debezium Connect REST API |
| rw-tempo | 3200 / 4317 / 4318 | Grafana Tempo (traces) |
| rw-pact-broker | 9292 | Pact Broker UI |
| rw-pgadmin | 5055 | pgAdmin 4 |
| rw-redis-commander | 8081 | Redis Commander |
| rw-identity-svc | 5070 | Identity API (JWT issuance) |
| rw-content-svc | 5060 | Content API |
| rw-bff-web | 5050 | BFF (public entry point) |

### Environment variables (per service)

Each service container receives the following categories of configuration via Compose `environment:` blocks:

- `ASPNETCORE_ENVIRONMENT=Development`
- `ConnectionStrings__<svc>=Host=postgres;Port=5432;Database=<svc>;Username=postgres;Password=postgres`
- `ConnectionStrings__rabbitmq=amqp://guest:guest@rabbitmq:5672/`
- `Vault__Enabled`, `Vault__Address`, `Vault__RoleIdPath`, `Vault__SecretIdPath`
- `Authentication__Jwks__JwksUri`, `Authentication__Jwks__Issuer`, `Authentication__Jwks__Audience`

Services that use Kafka (search-svc, webhooks-svc, bff-web) additionally receive:

- `Kafka__BootstrapServers=kafka:9092`
- `Kafka__GroupId=<svc>-cdc`

The content service receives S3 config pointing at LocalStack:

- `Storage__ServiceUrl=http://localstack:4566`
- `Storage__BucketName=content-dev`
- `Storage__ForcePathStyle=true`

### Startup order

Compose enforces health-check dependencies. The exact boot sequence is:

1. `postgres`, `redis`, `rabbitmq`, `vault`, `localstack`, `kafka`, `elasticsearch` start and become healthy.
2. `localstack-init` creates the `content-dev` S3 bucket (one-shot).
3. `vault-init` configures secrets engines, AppRoles, and writes per-service credential files into `deploy/compose/vault-creds/`.
4. `vault-seed` writes dev-mode static secrets.
5. Application services start.
6. `debezium-init` registers the three connector configurations via the Debezium Connect REST API.

Allow approximately 60–90 seconds on first boot for all health checks to pass.

### Stopping

```bash
docker compose down          # stop and remove containers, keep volumes
docker compose down -v       # also delete volumes (full reset)
```

---

## Option B: Aspire AppHost

The Aspire host at `deploy/aspire/Program.cs` provides a richer development experience: a live dashboard, structured logs per resource, and automatic endpoint injection between services. It runs all services as processes on the host (not in Docker), while infrastructure (Postgres, Redis, RabbitMQ, Kafka, Vault, Elasticsearch, etc.) runs in containers.

### Start

```bash
cd deploy/aspire
dotnet run
```

The Aspire dashboard is available at `http://localhost:18888` (or the URL printed to stdout).

The BFF is available at `http://localhost:5050` (HTTP) and `https://localhost:5051` (HTTPS).

### Persistent containers

Infrastructure containers use `ContainerLifetime.Persistent`. On the first `dotnet run` the containers start; on subsequent runs Aspire reattaches to the already-running containers without paying the startup cost again. This saves approximately 30 seconds per subsequent boot.

Persistent container volumes:

| Volume | Content |
|--------|---------|
| `ritualworks-platform-postgres-data` | All service databases |
| `ritualworks-platform-redis-data` | Redis persistence |
| `ritualworks-platform-kafka-data` | Kafka log segments |
| `ritualworks-platform-elasticsearch-data` | Elasticsearch indices |
| `ritualworks-platform-localstack-data` | LocalStack S3 buckets |
| `ritualworks-platform-pact-db-data` | Pact Broker database |

### Catalog replicas

Catalog is configured with `.WithReplicas(2)`. The BFF load-balances across both replicas via Aspire's reverse proxy. Each replica stamps `X-Instance-Id` on responses to demonstrate distribution.

### Resource log files

The `ResourceFileLogger` background service captures all resource logs to `logs/<resource-id>.log` relative to the AppHost output directory. Set `ASPIRE_LOGS_DIR` to redirect to a different path.

---

## Database setup

The file `deploy/aspire/init-postgres.sql` is bind-mounted into the Postgres container at `/docker-entrypoint-initdb.d/init.sql`. It runs automatically on first volume initialization.

What it does:

1. Creates one database per service with state: `catalog`, `orders`, `payments`, `content`, `identity`, `checkout`, `notifications`, `audit`, `location`, `webhooks`, `payouts`, `scheduler`, `privacy`, `merchant`.
2. Creates a `<db>_owner` NOLOGIN group role per database.
3. Transfers database ownership to the matching group role.
4. Grants full schema privileges and sets default privileges so EF Core migrations run without requiring superuser access.

Each service connects with credentials issued by Vault (dynamic users that Vault grants membership to the `<db>_owner` role). The static `postgres/postgres` credentials in Compose and Aspire are dev-only; production uses Vault dynamic credentials only.

Postgres is started with WAL logical replication enabled:

```
-c wal_level=logical
-c max_replication_slots=10
-c max_wal_senders=10
```

These flags are required for Debezium to attach replication slots.

---

## Vault dev mode

Both Compose and Aspire run Vault in `-dev` mode:

- Vault is auto-unsealed on startup.
- The bootstrap root token is `dev-root-token` (`VAULT_DEV_ROOT_TOKEN_ID`).
- Vault listens on `0.0.0.0:8200`.
- Do not use `dev-root-token` to authenticate services directly in development. The `vault-init` one-shot container configures AppRole auth and writes per-service `role_id` / `secret_id` credential files into `deploy/compose/vault-creds/<svc>/` (Compose) or `deploy/aspire/vault-creds/<svc>/` (Aspire). Services read these files at startup via `Vault__RoleIdPath` and `Vault__SecretIdPath`.

To inspect Vault interactively:

```bash
export VAULT_ADDR=http://localhost:8200
export VAULT_TOKEN=dev-root-token
vault status
vault auth list
vault secrets list
```

---

## Running tests locally

### Unit tests

No infrastructure required. Run from the repo root:

```bash
dotnet test RitualworksPlatform.sln \
  --filter "FullyQualifiedName~.Unit" \
  --configuration Release
```

Or run a single service:

```bash
dotnet test tests/Catalog/Catalog.Unit --configuration Release
```

### Integration tests

Require Docker (for Testcontainers). Each integration test project spins up its containers automatically via the shared container singletons. No manual infrastructure setup is needed.

```bash
dotnet test tests/Catalog/Catalog.Integration --configuration Release
```

Testcontainers uses `WithReuse(true)` on all shared containers, so the containers remain running after the test run finishes and are reattached on the next run.

To run all integration suites in parallel (mirrors CI):

```bash
find tests -name "*.Integration.csproj" | xargs -P 4 -I{} dotnet test {} --configuration Release
```

### Architecture tests

No infrastructure required:

```bash
dotnet test tests/Catalog/Catalog.Architecture --configuration Release
```

The architecture check script can also be run directly:

```bash
bash scripts/check-architecture.sh
```

### Contract (Pact) tests

No infrastructure required. Pact files are written to `tests/pacts/`:

```bash
dotnet test tests/Catalog/Catalog.Contract --configuration Release
```

### E2E tests

Require Docker (full Aspire stack) and Playwright browsers.

Install Playwright browsers once:

```bash
dotnet build tests/E2E/E2E.csproj
pwsh tests/E2E/bin/Debug/net9.0/playwright.ps1 install --with-deps
```

Run with the enable flag (tests skip by default when `E2E_ENABLED` is unset):

```bash
E2E_ENABLED=1 dotnet test tests/E2E/E2E.csproj --configuration Release
```

Smoke tests (no Playwright, exercises the BFF via HttpClient against the Aspire-hosted stack):

```bash
dotnet test tests/Smoke/Smoke.csproj --configuration Release
```

---

## Common issues and troubleshooting

**Postgres not starting**

`pg_isready` health check fails if the data volume was created by a different Postgres image version. Delete the volume and restart:

```bash
docker volume rm ritualworks-platform-postgres-data   # Aspire
# or
docker compose down -v                                # Compose
```

**Vault AppRole credentials not found**

The `vault-init` container must complete before any service that has `Vault__Enabled=true` starts. If services start before `vault-init` finishes, restart the affected containers:

```bash
docker compose restart identity-svc catalog-svc-1 catalog-svc-2
```

**Debezium connector registration fails**

The `debezium-init` container registers connectors against the Debezium Connect REST API. If Connect is not yet healthy when `debezium-init` runs, the connectors will not be registered. To re-register:

```bash
bash scripts/cdc.sh register deploy/aspire/debezium/catalog-connector.json
bash scripts/cdc.sh register deploy/aspire/debezium/orders-connector.json
bash scripts/cdc.sh register deploy/aspire/debezium/payments-connector.json
```

**Port conflicts**

If a host port is already in use, stop the conflicting process or change the host-side port in `docker-compose.yml`. Common conflicts: port 5432 (local Postgres), port 6379 (local Redis), port 9092 (local Kafka).

**Testcontainers cannot reach Docker**

Ensure `DOCKER_HOST` is set correctly (macOS with Docker Desktop uses the default socket at `/var/run/docker.sock`). Set `TESTCONTAINERS_RYUK_DISABLED=true` if the Ryuk resource reaper conflicts with the reuse pattern.

**Aspire service discovery not resolving**

Aspire injects service URLs at runtime via `IResourceBuilder.GetEndpoint()`. If a service starts before its dependency's endpoint is known, it may fail with a connection refused error. Use `.WaitFor()` chaining (already configured in `Program.cs`) and allow extra startup time on slower machines.
