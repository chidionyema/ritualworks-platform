# Vault Credential Delivery Architecture v2

> Status: **Approved** | Owner: Platform Team | Last updated: 2026-05-18

## Problem Statement

The platform had three competing secret delivery architectures (Vault Agent
sidecar, custom VaultSharp SDK, Fly Secrets) with 2,540 LOC of custom C# code.
A war-room review found three interconnected bugs that would crash all 7
Vault-enabled services on launch day. The root cause was designing for a
Kubernetes future while shipping on Fly.io, without making either path work
completely.

## Decision

Use a **two-tier, config-driven** model that works on Fly.io today and
Kubernetes tomorrow with zero code changes between platforms.

## Architecture

### Tier 1: KV Secrets (JWT keys, Stripe, OAuth, notification providers)

```
FLY.IO:  VaultConfigBootstrap → AppRole login → KV read → IConfiguration
K8S:     Vault Agent sidecar → /vault/secrets/*.json → AddVaultAgentSecrets → IConfiguration
```

Both paths produce the same IConfiguration shape. The switch is config-only:

| Setting | Fly value | K8s value |
|---------|-----------|-----------|
| `Vault:DeliveryMode` | `AppRole` | `AgentSidecar` |

### Tier 2: Database Credentials

```
NEON (managed PG):     Static password from bootstrap.sh → no rotation
VAULT-PG (sandbox):    Vault static roles → PeriodicPasswordProvider → rotation every 1h
K8S (self-managed PG): Vault Agent renders creds file → PeriodicPasswordProvider reads file
```

The switch is config-only:

| Setting | Neon value | Sandbox value | K8s value |
|---------|-----------|---------------|-----------|
| `Vault:DatabaseMode` | `None` (default) | `StaticRole` | `AgentFile` |

### What Each Mode Does

#### `DatabaseMode=None` (Fly/Neon — current production)

- NpgsqlDataSource uses the static connection string from `bootstrap.sh`
- No `UsePeriodicPasswordProvider` registered
- No Vault database engine involvement
- Connection string includes Host, Username, Password from Neon

#### `DatabaseMode=StaticRole` (vault-pg sandbox, future self-managed PG)

- NpgsqlDataSource wraps the connection string with `UsePeriodicPasswordProvider`
- Callback calls `VaultService.GetDatabaseCredentialsAsync(roleName)`
- VaultService calls Vault API: `GET /v1/database/static-creds/{roleName}`
- Vault rotates the password for the static role on `rotation_period` (1h)
- On callback failure: logs warning, returns static password from connection string
- Health check monitors lease TTL

#### `DatabaseMode=AgentFile` (K8s -- implemented)

- Vault Agent sidecar renders credentials to `/vault/secrets/db-{service}.json`
- `UsePeriodicPasswordProvider` reads the file every 1 minute via
  `VaultServiceCollectionExtensions.ReadAgentCredentialFileAsync`
- When Vault Agent rotates the file, next poll picks up new password
- Both `username` and `password` are read from the JSON; username changes
  are applied to the connection string builder on each rotation
- On file-not-found: returns last-known-good password (initial fallback is
  the static password from the connection string)
- On malformed JSON: logs warning, increments `vault.credential_rotation.failure`
  metric, returns last-known-good password

##### K8s Configuration

```yaml
# Pod annotations (Vault Agent Injector)
vault.hashicorp.com/agent-inject: "true"
vault.hashicorp.com/agent-inject-secret-db-orders.json: "database/static-creds/haworks-orders"
vault.hashicorp.com/agent-inject-template-db-orders.json: |
  {{ with secret "database/static-creds/haworks-orders" }}
  { "username": "{{ .Data.username }}", "password": "{{ .Data.password }}" }
  {{ end }}

# App environment
Vault__DatabaseMode: "AgentFile"
Vault__Agent__SecretsPath: "/vault/secrets"
```

## Service Integration Pattern

Every service calls the same two methods:

```csharp
// DependencyInjection.cs
services.AddVaultIntegration(configuration);           // AppRole auth, KV, token lifecycle
services.AddVaultNpgsqlDataSource(connectionString, "haworks-orders");  // DB with mode-based rotation
```

Services that need startup KV secrets (Identity, Payments, Notifications)
add a pre-DI bootstrap:

```csharp
// Program.cs (before builder.Build())
var secrets = await VaultConfigBootstrap.LoadAsync(configuration, new[] {
    new KvMapping("identity/jwt", "Jwt"),
    new KvMapping("identity/oauth/google", "Authentication:Google", Optional: true),
});
builder.Configuration.AddInMemoryCollection(secrets);
```

## Static vs Dynamic Roles

**We use STATIC roles.** Rationale:

- Static roles have a fixed username (e.g., `identity_owner`) with Vault rotating
  only the password. This is correct for long-running application servers.
- Dynamic roles create ephemeral usernames (`v-approle-haworks-id-AbCdEf`) that
  break EF migrations and connection pooling assumptions.
- The C# code calls `GetStaticCredentialsAsync()` which hits
  `/v1/database/static-creds/`. This matches `database/static-roles/` in Vault.
- Both dev (`vault-init.sh`) and prod (`seed.sh`) now create static roles.

## Failure Modes

| Scenario | Behavior | User impact |
|----------|----------|-------------|
| Vault down on service boot | VaultConfigBootstrap retries 5x with backoff (62s). If all fail, service crashes with descriptive error. | Service unavailable until Vault recovers. Fly auto-restarts. |
| Vault down during rotation | PeriodicPasswordProvider catch returns static password. Circuit breaker opens. Health check → Degraded. | Zero downtime. Stale credentials used. Alert fires. |
| Vault sealed | Same as "down". entrypoint.sh auto-unseals from `VAULT_UNSEAL_KEY`. | Brief control-plane outage (5-15s). |
| Wrapping token expired | VaultConfigBootstrap logs descriptive error with `WRAP_TTL_SECONDS` reference. Service crashes. | Re-run `ci-stage-vault-creds.sh` to issue fresh token. |
| Wrong role type in Vault | `GetStaticCredentialsAsync` returns 404. PeriodicPasswordProvider catch logs warning, uses static password. | Rotation silently disabled. Alert fires on `vault.credential_rotation.failure`. |
| Neon password change | Operator runs `bootstrap.sh` with new `POSTGRES_BASE`. Fly redeploys services. | Brief restart per service (~10s rolling). |

## Configuration Reference

### Environment Variables (Fly Secrets via bootstrap.sh)

```
Vault__Enabled=true
Vault__Address=http://haworks-vault.internal:8200
Vault__DatabaseMode=None                    # None | StaticRole | AgentFile
Vault__RoleId=<uuid>                        # From ci-stage-vault-creds.sh
Vault__SecretId=<wrapped-token>             # From ci-stage-vault-creds.sh
Vault__SecretIdIsWrapped=true
ConnectionStrings__<service>=Host=...;Password=...;SslMode=Require
```

### Files That Own This System

| File | Responsibility |
|------|---------------|
| `src/BuildingBlocks/Vault/VaultConfigBootstrap.cs` | Startup KV secret loading |
| `src/BuildingBlocks/Vault/VaultServiceCollectionExtensions.cs` | DI registration, mode-based NpgsqlDataSource |
| `src/BuildingBlocks/Vault/VaultService.cs` | Runtime credential fetch, AppRole token lifecycle |
| `src/BuildingBlocks/Vault/VaultAppRoleAuthenticator.cs` | AppRole HTTP login |
| `src/BuildingBlocks/Vault/VaultClientFactory.cs` | VaultSharp client creation, wrapping token unwrap |
| `src/BuildingBlocks/Vault/VaultCredentialProvider.cs` | Cached static-role credential fetcher |
| `src/BuildingBlocks/Vault/VaultLeaseHealthCheck.cs` | Lease TTL health check |
| `deploy/vault/seed.sh` | Vault static role + AppRole + KV seeding |
| `deploy/fly/ci-stage-vault-creds.sh` | CI credential staging with wrapped secret_ids |
| `deploy/fly/bootstrap.sh` | First-time Fly app setup + static secret staging |
| `infra/vault/services.json` | Master service list (single source of truth) |
| `infra/vault/database/roles.json` | Static database role definitions |

## Monitoring

### Metrics (OpenTelemetry → Prometheus)

| Metric | Type | Alert threshold |
|--------|------|----------------|
| `vault.auth.failure` | Counter | >0 for 5min |
| `vault.credential_rotation.failure` | Counter | >0 for 10min |
| `vault.lease.ttl_remaining_seconds` | Gauge | <300s |
| `vault.circuit_breaker.state` | Gauge | ==2 (open) for 1min |

### Health Endpoint

`GET /health` includes `vault-lease-{roleName}` when `DatabaseMode=StaticRole`:
- **Healthy:** Credentials fresh
- **Degraded:** TTL >90% elapsed
- **Unhealthy:** Credentials expired

### Structured Logs

Every Vault operation logs with `{Service, Role, DurationMs, Outcome}`.
Search in Loki: `{app="haworks-identity"} |= "VaultCredentialRotated"`.

## Roadmap

| Phase | What | When |
|-------|------|------|
| 1 | Fix 3 bugs + DatabaseMode enum | Done (this PR) |
| 2 | Delete dead code (VaultRotatingConnectionStringProvider, SecureStringExtensions, CredentialStore) | Next sprint |
| 3 | Add VaultMetrics + OpenTelemetry instrumentation | Next sprint |
| 4 | Integration tests with Testcontainers Vault | Next sprint |
| 5 | Enable `StaticRole` on vault-pg sandbox | Done (this PR) |
| 6 | Implement `AgentFile` mode for K8s | Done (this PR) |

## Enabling/Disabling StaticRole Mode

### Enable for a service

Run the helper script to stage Vault database rotation secrets on a Fly app:

```bash
./scripts/enable-vault-db-rotation.sh identity
```

This stages `Vault__DatabaseMode=StaticRole` and vault-pg connection details.
Deploy the service to activate. The service will call
`GET /v1/database/static-creds/haworks-identity` on each rotation interval.

### Prerequisites

1. **vault-pg deployed** with init.sql (creates Postgres users):
   `fly deploy -c fly.vault-pg.toml`
2. **vault deployed** with seed.sh (creates static roles in Vault):
   `fly deploy -c fly.vault.toml`
3. **Service has Vault enabled**: `Vault__Enabled=true` already staged.

### Disable (revert to Neon)

```bash
flyctl secrets unset -a haworks-identity \
  Vault__DatabaseMode Database__Host Database__Port Database__Database Database__SslMode
```

The service falls back to `DatabaseMode=None` and uses its static connection string.
