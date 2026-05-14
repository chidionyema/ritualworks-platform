# Production Deployment

## Fly.io app topology

Every service is deployed as an independent Fly.io app. There is no shared runtime process; each app has its own `fly.<svc>.toml`, its own VM pool, and its own Fly secrets namespace.

### App inventory

| Fly app name | toml file | Notes |
|---|---|---|
| `ritualworks-bffweb` | `fly.bffweb.toml` | Public entry point; HTTPS enforced |
| `ritualworks-identity` | `fly.identity.toml` | Internal only; no public IP |
| `ritualworks-catalog` | `fly.catalog.toml` | Internal only |
| `ritualworks-orders` | `fly.orders.toml` | Internal only |
| `ritualworks-payments` | `fly.payments.toml` | Internal only |
| `ritualworks-checkout` | `fly.checkout.toml` | Internal only |
| `ritualworks-search` | `fly.search.toml` | Internal only |
| `ritualworks-notifications` | `fly.notifications.toml` | Internal only |
| `ritualworks-audit` | `fly.audit.toml` | Internal only |
| `ritualworks-webhooks` | `fly.webhooks.toml` | Internal only |
| `ritualworks-vault` | `fly.vault.toml` | HashiCorp Vault in production mode |
| `ritualworks-vault-pg` | `fly.vault-pg.toml` | Postgres backend for Vault storage |
| `ritualworks-meilisearch` | `fly.meilisearch.toml` | Meilisearch (separate from Elasticsearch) |

All internal services are reachable from each other on Fly's private 6PN network via `http://<app-name>.internal:8080`. The BFF uses `.internal` hostnames (not `.flycast`) because the load-balanced `.flycast` endpoint was returning connection resets for HTTP/8080 traffic in this cluster.

### VM sizing (BFF example, from `fly.bffweb.toml`)

```toml
[[vm]]
  memory   = "512mb"
  cpu_kind = "shared"
  cpus     = 1
```

All backend services use the same default sizing. Adjust per-app in the corresponding toml file.

### Auto-stop / auto-start

Non-critical services use `auto_stop_machines = "stop"` and `min_machines_running = 0` to reduce idle cost. Identity is configured with `auto_stop_machines = "off"` and `min_machines_running = 1` to ensure it is always available for JWT validation.

---

## CI/CD pipeline

The pipeline is split across two GitHub Actions workflows:

```
push to main
    |
    v
CI workflow (.github/workflows/ci.yml)
    - Build
    - Unit tests
    - Architecture tests
    - Contract (Pact) tests
    - Integration tests (parallel matrix, one job per service)
    |
    | on: workflow_run + conclusion == 'success'
    v
Deploy workflow (.github/workflows/deploy.yml)
    - Detect changed paths
    - Plan service matrix
    - Deploy vault-pg (if changed)
    - Deploy vault (if changed)
    - Stage vault creds (if vault was redeployed)
    - Deploy backends in parallel matrix
    - Deploy BFF (after all backends)
```

The Deploy workflow also triggers on `workflow_dispatch` for manual forced deploys. A `force_all` input (default `true` on dispatch) deploys every service regardless of changed paths.

### CI workflow structure

**Job 1: `build-and-fast-tests`** (runs on every push/PR, timeout 15 minutes)

- `dotnet restore` + `dotnet build --configuration Release`
- Unit tests: all `*.Unit/*.csproj` projects
- Architecture tests: all `*.Architecture/*.csproj` projects
- Contract (Pact) tests: all `*.Contract/*.csproj` projects

**Job 2: `integration-tests`** (parallel matrix, timeout 20 minutes per suite, requires job 1)

One job per service:

```
Catalog, Orders, Payments, Identity, BffWeb, CheckoutOrchestrator,
Content, Search, Audit, Notifications, Webhooks, Payouts, Scheduler
```

Each suite restores and builds only its own `.Integration.csproj`, then runs it. Docker images are cached in `actions/cache` keyed on exact image tags (`postgres:16-alpine`, `elasticsearch:8.17.0`, `cp-kafka:7.6.1`, `rabbitmq:3-management`) to avoid re-pulling on every run.

`TESTCONTAINERS_RYUK_DISABLED=true` is set globally to prevent interference with the `WithReuse(true)` container pattern.

**Job 3: `smoke-and-e2e`** (manual dispatch only, timeout 25 minutes)

Spins up the full Aspire AppHost in-process, installs Playwright browsers, then runs Smoke and E2E test suites. This job is excluded from automatic push/PR runs because the full Aspire stack does not reliably bootstrap within the CI timeout on headless GitHub runners. Once a Fly deploy is stable, the intent is to trigger E2E tests against `SMOKE_TARGET_URL` instead.

### Deploy workflow jobs

**`changes`**: Uses `dorny/paths-filter` to determine which service paths changed in the push. The `shared` filter covers `src/BuildingBlocks/**`, `src/Contracts/**`, `Directory.Build.props`, and the deploy workflow file itself. A change to shared paths triggers all services.

**`plan`**: Builds the deployment matrix as a JSON array of service names. Services are added to the matrix only if their own paths or shared paths changed, or if `force_all` is true. BFF and Vault have separate boolean outputs because they deploy in a fixed order (Vault before backends, backends before BFF).

**`deploy-vault-pg`** and **`deploy-vault`**: Run `flyctl deploy -c fly.<svc>.toml --remote-only`. The `--remote-only` flag means Fly builds the Docker image on its own builders rather than uploading a local image.

**`stage-vault-creds`**: Runs `deploy/fly/ci-stage-vault-creds.sh` to capture Vault init keys and AppRole credentials and stage them as Fly secrets on the identity app. This only runs when Vault was redeployed in the same workflow run (no need to re-capture when Vault was not touched). The step can be bypassed with `bypass_vault_capture: true` on `workflow_dispatch`.

**`deploy-backends`**: Parallel matrix job, one runner per service in the plan matrix. `fail-fast: false` ensures a failure in one service does not cancel the others. All backend jobs run after Vault jobs settle (using `always()` + skipped-result handling so a skipped Vault step does not block backends).

**`deploy-bff`**: Runs after `deploy-backends` completes or is skipped. The BFF is always deployed last because it depends on all backends being healthy before it can route traffic.

---

## Secret management

### Vault in production

Vault runs as a Fly app (`ritualworks-vault`) backed by a Postgres storage backend (`ritualworks-vault-pg`). It is not in dev mode; it must be initialized with `vault operator init` and unsealed with `vault operator unseal` after each restart.

Services authenticate to Vault using AppRole: each service has a `role_id` and `secret_id` staged as Fly secrets. On startup, the service exchanges these for a short-lived Vault token, then uses that token to fetch dynamic database credentials and other secrets.

The `vault-init.sh` script configures:

- The KV secrets engine for per-service static secrets
- The database secrets engine with per-service roles that issue dynamic Postgres credentials
- AppRole auth with per-service roles

The `deploy/fly/ci-stage-vault-creds.sh` script runs in CI after a Vault deploy to capture the current AppRole credentials and stage them as Fly secrets on the identity app (which distributes them to the other services via the Fly secrets API).

### Fly secrets

Secrets that cannot be stored in Vault (because they are needed to bootstrap Vault itself, or because they are external API keys) are stored as Fly secrets set via `flyctl secrets set`:

```bash
flyctl secrets set STRIPE_SECRET_KEY=sk_live_... -a ritualworks-payments
flyctl secrets set OTEL_EXPORTER_OTLP_ENDPOINT=https://... -a ritualworks-bffweb
```

Fly secrets are injected as environment variables at VM startup. They are not visible in the toml files or the repository.

Non-secret configuration (internal hostnames, OTEL service names, ASP.NET environment) is stored in the `[env]` block of each toml file and is committed to the repository.

---

## Deploy flow: path filtering

A typical single-service PR produces this deploy plan:

```
changed paths: src/Catalog/**
plan matrix:   ["catalog"]
vault-pg:      skipped
vault:         skipped
stage-creds:   skipped
backends:      deploy catalog
bff:           skipped
```

Total wall-clock time for a single-service deploy: approximately 90 seconds.

A change to `src/BuildingBlocks/**` or `src/Contracts/**` triggers all services:

```
changed paths: src/BuildingBlocks/**
plan matrix:   ["identity","catalog","orders","payments","checkout",
                "search","notifications","audit","webhooks"]
backends:      parallel matrix (all services)
bff:           deploy after all backends
```

### Content service gate

The content service deploy is gated on `vars.DEPLOY_CONTENT == 'true'` (a GitHub Actions repository variable, not a secret). Set this variable in the GitHub repository settings to enable content-svc deploys. This allows deferring content-svc deployment independently of the rest of the platform.

---

## Rollback strategy

Fly maintains a release history per app. To roll back a single service to the previous release:

```bash
flyctl releases list -a ritualworks-catalog
flyctl deploy --image <previous-image-ref> -a ritualworks-catalog
```

Because the Deploy workflow is triggered by CI success, a broken commit will not reach production if any CI job fails. For a bad deploy that passes CI:

1. Identify the last known-good commit SHA.
2. Run `workflow_dispatch` on the Deploy workflow with `force_all: true` after resetting the branch to that SHA, or roll back individually using `flyctl deploy --image`.

Database migrations are applied by the service on startup via EF Core `MigrateAsync()`. Migrations are additive (no destructive schema changes in a single release). Rolling back a service version after a migration has applied leaves the new schema columns in place but they will be ignored by the previous version.

---

## Monitoring and health checks

### Health endpoints

Every service exposes ASP.NET Core health check endpoints:

- `GET /healthz` — liveness (always returns 200 if the process is running)
- `GET /readyz` — readiness (checks database connectivity, message bus, and any critical dependencies)

Fly.io uses the `[http_service]` section of each toml to route traffic. Services with `auto_stop_machines = "stop"` are automatically woken by incoming requests.

### OpenTelemetry

All services export traces via OTLP to Grafana Tempo. The OTLP endpoint is configured via:

```bash
flyctl secrets set OTEL_EXPORTER_OTLP_ENDPOINT=https://<tempo-endpoint> -a <app-name>
```

Each app sets its resource identity in the toml `[env]` block:

```toml
OTEL_SERVICE_NAME        = "catalog-svc"
OTEL_RESOURCE_ATTRIBUTES = "deployment.environment=production,service.namespace=ritualworks"
```

### Structured logging

All services use Serilog with correlation ID enrichment. Logs are available in the Fly dashboard (`flyctl logs -a <app-name>`) and can be streamed to an external sink by configuring a Serilog sink via Fly secrets.

### Fly machine monitoring

```bash
flyctl status -a ritualworks-bffweb      # machine state and health
flyctl vm list -a ritualworks-catalog    # list all VMs with status
flyctl logs -a ritualworks-payments      # tail logs
```
