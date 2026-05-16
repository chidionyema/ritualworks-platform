# 02 — Platform: Local Dev, CI/CD, Deploy, Observability, Secrets

## Two Parallel Deployment Paths

The repo carries two independent ways to run the system. Both work all the time. Neither obviates the other.

| Path | Use case | Cost | Startup |
|---|---|---|---|
| `dotnet run --project deploy/aspire` | **Daily dev iteration.** One process tree, live reload, Aspire dashboard with traces/logs/metrics. | Free | <30 s |
| `make k8s-up` | **Production-shape demo.** kind cluster + ArgoCD GitOps + Vault K8s auth + Helm + per-service Postgres StatefulSets. | Free locally; ~$12/mo on DigitalOcean for a public demo | ~3 min |

Same image artifacts, same Helm `values.yaml` shape, same Vault policies, same OTel pipeline. The only difference is the runtime substrate.

---

## Local Dev (Aspire) — New AppHost

### What the existing monolith's AppHost looks like (reference)

The existing monolith's `src/haworks.AppHost/Program.cs` (in the original repo) declares one resource graph that the new AppHost will model on:

| Resource | Type | Notes |
|---|---|---|
| `postgres` | container | `AddPostgres("postgres")` with `WithDataVolume`, `WithPgAdmin`, `init-postgres.sql` mount. Adds 5 logical databases: `catalog`, `orders`, `payments`, `content`, `identity`. |
| `redis` | container | `AddRedis("redis")` with `WithRedisCommander`. |
| `rabbitmq` | container | `AddRabbitMQ("rabbitmq", port: 5672)` with management plugin. **Pinned port** to dodge DCP drift. |
| `minio` | container | S3 + console endpoints. |
| `vault` | container | Dev mode, auto-unsealed, AppRole-bootstrapped. |
| `vault-init` | one-shot container | `WaitFor(vault, postgres)`. Runs `vault-init.sh` to enable AppRole, seed KV mount, configure DB secrets engine, write role_id/secret_id to host bind-mount. |
| `vault-seed` | one-shot container | `WaitForCompletion(vaultInit)`. Runs `seed-vault-dev.sh` to write actual secret values (Stripe, JWT, OAuth, hub). |
| `clamav` | container | Virus scanning. |
| `api` | project | `AddProject<haworks>("api")`. Single .NET process; references all 5 DbContexts; consumes everything. `WaitForCompletion(vaultInit, vaultSeed)`. |

### What the new AppHost looks like

The new AppHost lives at `deploy/aspire/Program.cs` in the new repo. The infrastructure resources (postgres, redis, rabbit, minio, vault, vault-init, vault-seed, clamav) are lifted nearly verbatim — they serve all services collectively. The single `AddProject<haworks>("api")` becomes **eight project references** (7 services + 1 BFF), each wired to its own subset of dependencies:

```csharp
var identity = builder.AddProject<Projects.Identity_Api>("identity-svc")
    .WaitForCompletion(vaultSeed)
    .WithReference(identityDb)
    .WithEnvironment("Vault__RoleIdPath",   $"{credsHostDir}/identity/role_id")
    .WithEnvironment("Vault__SecretIdPath", $"{credsHostDir}/identity/secret_id");

var catalog = builder.AddProject<Projects.Catalog_Api>("catalog-svc")
    .WaitForCompletion(vaultSeed)
    .WithReference(catalogDb)
    .WithReference(redis)
    .WithEnvironment("Vault__RoleIdPath",   $"{credsHostDir}/catalog/role_id")
    .WithEnvironment("Vault__SecretIdPath", $"{credsHostDir}/catalog/secret_id");

// ... orders, payments, content, checkout-orchestrator, bff-web
```

**Plus a new `pact-broker` resource** wired alongside RabbitMQ for contract publishing.

The `vault-init.sh` script (set up in Phase 0 — see [03-build-plan.md](./03-build-plan.md)) provisions **per-service AppRoles** and writes each service's credentials to its own subdirectory: `vault-creds/<service>/role_id`, `vault-creds/<service>/secret_id`. The AppHost wires each service to its own credential paths from day 1.

### The override pattern

For the "I'm hacking on catalog-svc against the rest of the world" case:

```bash
# Default: all services run as project references (live reload everywhere)
dotnet run --project deploy/aspire

# Single-service-from-source, rest from published images
dotnet run --project deploy/aspire -- --override catalog=local
# Catalog runs as AddProject; identity/orders/payments/etc. run as AddContainer with pinned image digests from image-digests.lock
```

Implementation: AppHost reads `--override` args, swaps `AddProject<T>` for `AddContainer(image-tag)` per service. Image digests pinned via `infra/image-digests.lock` (committed; updated by Renovate).

This is the **portability hack that makes Aspire work in monorepo + production-image mode**. Without it, every dev would always need every service's source compiled — slow and tedious.

### Aspire dashboard preserved

The current dashboard (traces, logs, metrics, resource graph) Just Works because each service uses `ServiceDefaults.Extensions.AddServiceDefaults()` — the OTel registration, health endpoints, and service discovery are inherited unchanged.

---

## Production-Shape Local: kind + ArgoCD + Helm

### What runs

```
kind cluster (single-node, ~3 GB RAM)
└── namespaces:
    ├── argocd                 ← ArgoCD itself + App-of-Apps
    ├── vault                  ← Vault server (StatefulSet)
    ├── haworks            ← all 7 services + 7 Postgres StatefulSets
    ├── observability          ← OTel collector, Tempo, Loki, Prometheus, Grafana
    └── pact                   ← Pact broker + its Postgres
```

### `make k8s-up` flow

```
1. kind create cluster --config infra/kind/cluster.yaml
2. kubectl apply -k deploy/argocd/install      # ArgoCD bootstrap
3. kubectl apply -f deploy/argocd/app-of-apps.yaml
4. ArgoCD syncs everything else from Git:
    • Vault (with K8s auth method enabled)
    • OTel collector + Tempo/Loki/Prometheus/Grafana
    • Pact broker
    • Per-service Postgres StatefulSets
    • Per-service Helm releases (one Application per service)
5. ArgoCD UI port-forwarded to localhost:8080
6. Grafana port-forwarded to localhost:3000
7. Smoke test: ./scripts/k8s-smoke.sh exercises checkout end-to-end
```

### Helm charts — per-service shape

```
deploy/helm/<service>/
├── Chart.yaml
├── values.yaml          ← defaults (kind-friendly: localhost registry, low resources)
├── values.prod.yaml     ← EKS overrides (ECR registry, prod resources, HPA enabled)
└── templates/
    ├── deployment.yaml
    ├── service.yaml
    ├── serviceaccount.yaml      ← Vault K8s auth bound to this SA
    ├── networkpolicy.yaml       ← only allow expected service-to-service traffic
    ├── poddisruptionbudget.yaml
    ├── horizontalpodautoscaler.yaml  ← prod only (commented out in kind)
    ├── servicemonitor.yaml      ← Prometheus scrape config
    └── README.md                 ← what knobs this chart exposes
```

The point: **same chart, different values.** "Deploy to EKS" = "ArgoCD points at this repo, uses `values.prod.yaml`." No code changes.

### ArgoCD App-of-Apps

```
deploy/argocd/
├── app-of-apps.yaml         ← root Application that syncs everything below
└── applications/
    ├── vault.yaml
    ├── observability.yaml
    ├── pact-broker.yaml
    ├── identity-svc.yaml    ← points at deploy/helm/identity/
    ├── catalog-svc.yaml
    ├── ... etc
```

ArgoCD UI shows 7 services synced from Git, with diff/sync status and manual rollback per Application. Recruiter screenshot material.

---

## CI/CD — Monorepo with Path Filters

### Single workflow, path-aware jobs

`.github/workflows/ci.yml` uses `dorny/paths-filter@v3` to determine which jobs run:

| Path changed | Jobs triggered |
|---|---|
| `src/Catalog/**` | catalog-unit, catalog-integration, catalog-architecture, catalog-contract-publish, catalog-image-build |
| `src/Contracts/**` or `src/BuildingBlocks/**` | **all services'** unit + integration + architecture (cross-cutting) |
| `tests/E2E/**` or `deploy/**` | E2E + k8s-smoke against ephemeral kind |
| Any | Pact `can-i-deploy` check (cheap, always runs) |

### Required PR checks (cannot merge without)

1. All affected services' unit + integration + architecture tests green.
2. Affected services' Pact contracts published to broker.
3. `pact-broker can-i-deploy --pacticipant <each affected service> --version <sha> --to-environment production` returns can-deploy: true. **Prevents merging schema changes that break a known consumer.**
4. Cross-service E2E suite green against ephemeral kind cluster (only when `src/`, `deploy/`, or `tests/E2E/` changed).

### Image build & publish

Per service, on push to `main`:

```yaml
- uses: docker/build-push-action@v5
  with:
    context: src/Catalog
    file: src/Catalog/Catalog.Api/Dockerfile
    tags: |
      ghcr.io/haworks/catalog-svc:${{ github.sha }}
      ghcr.io/haworks/catalog-svc:main
    cache-from: type=gha
    cache-to: type=gha,mode=max

- name: Sign image
  run: cosign sign --yes ghcr.io/haworks/catalog-svc:${{ github.sha }}

- name: SBOM
  run: syft ghcr.io/haworks/catalog-svc:${{ github.sha }} -o spdx-json > sbom.json

- name: Trivy scan
  uses: aquasecurity/trivy-action@master
  with:
    image-ref: ghcr.io/haworks/catalog-svc:${{ github.sha }}
    exit-code: 1
    severity: HIGH,CRITICAL
```

### Dev image-digest lock-file

When a service publishes a new image, an auto-PR updates `infra/image-digests.lock`:

```
catalog-svc:    sha256:abc...  (built 2026-05-02, main@a1b2c3)
identity-svc:   sha256:def...  (built 2026-05-02, main@d4e5f6)
...
```

Devs running `dotnet run --project deploy/aspire --override catalog=local` get the catalog they edited; everything else is the pinned image digest. Renovate-bot opens the PR; the workflow auto-merges if all checks pass.

---

## Observability

### Stack

| Concern | Tool | Where in the repo |
|---|---|---|
| Traces | OTel Collector → Tempo → Grafana | `infra/observability/tempo/` |
| Logs | Serilog JSON → Promtail → Loki → Grafana | `infra/observability/loki/` |
| Metrics | Prometheus scrapes `/metrics` → Grafana | `infra/observability/prometheus/` |
| Dashboards | Grafana JSON, deployed via grafana-operator | `infra/observability/dashboards/` |
| Alerts | Prometheus alertmanager → Slack webhook (dev: stdout) | `infra/observability/alerts/` |

### Service-side

`Haworks.BuildingBlocks.AddServiceDefaults()` (lifted and renamed from `src/haworks.ServiceDefaults/Extensions.cs`) registers:
- OTel tracing with `AddSource("MassTransit")`, `AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation`, `AddEntityFrameworkCoreInstrumentation`
- OTLP exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (Aspire dashboard in dev, OTel collector in prod)
- `/health/ready` and `/health/live` endpoints
- Service discovery via Aspire conventions

**Critical: trace propagation through the outbox.** When orders-svc publishes `OrderCreated` and checkout-orchestrator-svc consumes it, the trace must connect. MassTransit propagates W3C `traceparent` through message headers if and only if both ends opt in. **Codify this in `Haworks.BuildingBlocks.Messaging`** so no service can register MT without OTel instrumentation. Add a synthetic test: publish from svc-A, assert consumer span on svc-B shares trace ID. Fails CI if broken.

### Dashboards

Two layers:
1. **Platform overview** (one dashboard, owned by docs/) — request rate, error rate, P99 per service, RabbitMQ queue depths, Vault token expiry, per-service Postgres connections.
2. **Per-service dashboard** (one per service folder) — service-specific metrics. Ships in `src/<Service>/dashboards/` and is deployed by grafana-operator.

### Alerts

Per-service alert rules live in `src/<Service>/alerts/*.yaml`. Platform-wide alerts (any service down, RabbitMQ unreachable, Vault sealed, Pact broker unreachable) live in `infra/observability/alerts/`.

---

## Secrets: Vault Topology

### Two auth methods, one Vault per environment

| Environment | Vault auth | How creds reach the pod |
|---|---|---|
| **Dev (Aspire)** | AppRole | `vault-init.sh` writes per-service `role_id` + `secret_id` files to `vault-creds/<svc>/`; AppHost binds path into each service's env vars; service reads at startup. |
| **Prod (kind / EKS)** | Kubernetes auth | Pod's ServiceAccount JWT becomes the credential. No `role_id`/`secret_id` files. Vault Agent Injector mutates the pod to mount secrets. |

Same Vault policies (`infra/vault/policies/*.hcl`), same role definitions, same KV layout. Only the auth method changes.

### KV path ownership

```
secret/identity/*    ← writable by identity-svc CI deploy role; readable by identity-svc runtime role
secret/catalog/*     ← writable by catalog-svc CI deploy role; readable by catalog-svc runtime role
... etc
secret/shared/jwt/*  ← writable by identity-svc only; readable by ALL services (JWT verification)
```

`VaultConfigBootstrap` (lifted into `Haworks.BuildingBlocks`) reads paths from a per-service `s_kvMappings` array — each service declares only the paths it needs.

### Dev seeding

`scripts/seed-vault-dev.sh` is split into per-service fragments under `infra/vault/secrets/<service>.dev.json`. The `vault-seed` Aspire container concatenates all fragments at boot. Each service repo (folder) contributes its own fragment — adding a new secret is one PR, doesn't touch a central script.

### Authentication & JWT

**Switch from symmetric (HS256) to asymmetric (RS256/ES256) JWTs.**

- identity-svc generates the RSA keypair, stores private key in Vault `secret/identity/jwt-signing`.
- identity-svc exposes `/.well-known/jwks.json` (JWKS endpoint).
- Every other service validates JWTs against JWKS — fetches once, caches with TTL, refreshes on rotation.
- **No shared secret to rotate across N services.** Rotation = identity-svc generates new key, publishes new JWKS, old key honored for a deprecation window.

For the rare cross-service shared secret that must stay symmetric (e.g., HubSecurity HMAC), version it in Vault (`secret/shared/hub:v2`) and accept N and N-1 during rollover.

---

## Self-Hosted Pact Broker

### Deployment

```yaml
# infra/pact-broker/docker-compose.yml
services:
  pact-broker:
    image: pactfoundation/pact-broker:latest
    ports: [ "9292:9292" ]
    environment:
      PACT_BROKER_DATABASE_URL: postgres://pact:pact@pact-db:5432/pact
      PACT_BROKER_BASE_URL: http://localhost:9292
    depends_on: [ pact-db ]

  pact-db:
    image: postgres:16-alpine
    environment:
      POSTGRES_USER: pact
      POSTGRES_PASSWORD: pact
      POSTGRES_DB: pact
    volumes: [ pact-db-data:/var/lib/postgresql/data ]

volumes:
  pact-db-data:
```

Wired into Aspire AppHost via `AddContainer("pact-broker", ...)` so `dotnet run --project deploy/aspire` brings it up alongside everything else.

In kind: same `pactfoundation/pact-broker` image, deployed via Helm chart in `deploy/helm/pact-broker/`. ArgoCD-managed.

### CI integration

```yaml
- name: Publish Pact contracts
  run: |
    pact-broker publish ./pacts \
      --consumer-app-version ${{ github.sha }} \
      --branch ${{ github.head_ref || github.ref_name }} \
      --broker-base-url ${{ vars.PACT_BROKER_URL }}

- name: Can-I-Deploy gate
  run: |
    pact-broker can-i-deploy \
      --pacticipant ${{ inputs.service }} \
      --version ${{ github.sha }} \
      --to-environment production \
      --broker-base-url ${{ vars.PACT_BROKER_URL }}
```

Webhook fires on `contract_content_changed` → triggers downstream consumer-pipeline runs, ensuring the new schema is verified before any merge.

### Dashboard as portfolio artifact

The Pact broker UI matrix view, showing all 13 events green across 7 services, is itself a portfolio screenshot. Lives in the README hero.

---

## Risks Specific to the Platform

See [05-risks.md](./05-risks.md) for the full ranked list. Platform-specific top three:

1. **Aspire's project-reference assumption breaks the polyrepo dev story silently.** Mitigated by the `--override` pattern (default = images, override = source).
2. **Trace context drops at the outbox boundary.** Mitigated by mandatory OTel registration in `BuildingBlocks.Messaging` + a synthetic test that fails CI if broken.
3. **Secret rotation coordination across services.** Mitigated by JWT RS256 + JWKS (eliminates shared-secret rotation entirely).

---

## See also

- [01-architecture.md](./01-architecture.md) — service map, the saga, communication patterns
- [03-build-plan.md](./03-build-plan.md) — when each platform piece lands
- [04-testing-strategy.md](./04-testing-strategy.md) — local-deploy parity test
- [adr/0002-aspire-local-kind-prod.md](./adr/0002-aspire-local-kind-prod.md)
- [adr/0005-jwt-rs256-jwks.md](./adr/0005-jwt-rs256-jwks.md)
- [adr/0006-self-hosted-pact-broker.md](./adr/0006-self-hosted-pact-broker.md)
