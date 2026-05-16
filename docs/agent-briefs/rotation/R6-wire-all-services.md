# R6 — Wire VaultRotatingCredentials Into All Services

**Brief:** R6 | **Spec:** `docs/agent-briefs/secret-rotation-spec.md`
**Phase:** 4 (sequential — requires R2 complete)
**Time budget:** 30 min

---

## Inputs

Read before touching anything:
- `infra/vault/services.json` — 8 services, `has_db` flags (7 have DB)
- `src/BuildingBlocks/BuildingBlocks.Vault/` (created by R2)
- Each service's `Infrastructure/DependencyInjection.cs` file for services with `has_db: true`:
  - identity, catalog, orders, payments, content, checkout-orchestrator, notifications

---

## Deliverable

For each of the 7 services with `has_db: true`, modify `Infrastructure/DependencyInjection.cs`:

1. Replace static `connectionString` lookup with `AddVaultRotatingPostgres(roleName, configuration)` from `BuildingBlocks.Vault`.
2. Register `IVaultClient` (VaultSharp) pointing at `VAULT_ADDR` env var (default `http://haworks-vault.internal:8200` on Fly; `http://vault:8200` in Docker Compose).
3. Keep the static connection string as a fallback: if `VAULT_ENABLED=false` (local dev without Vault), skip the rotating provider and use the static string. This ensures developer experience is not broken.

```csharp
if (configuration.GetValue<bool>("Vault:Enabled", defaultValue: false))
{
    services.AddVaultRotatingPostgres(roleName: "haworks-identity", configuration);
}
else
{
    var connectionString = configuration.GetConnectionString("identity")
        ?? throw new InvalidOperationException("ConnectionStrings:identity required when Vault:Enabled=false");
    services.AddNpgsqlDataSource(connectionString);
}
```

4. **notifications-svc**: add `SecretExpiryWarningEvent` consumer that sends a Slack alert notification (use existing notification provider wiring — `INotificationService` or equivalent in notifications-svc).

5. **Add to `docker-compose.yml`** (or the observability overlay):
   ```yaml
   environment:
     VAULT_ENABLED: "true"
     VAULT_ADDR: "http://vault:8200"
     VAULT_ROLE_ID: "${IDENTITY_VAULT_ROLE_ID}"
     VAULT_SECRET_ID: "${IDENTITY_VAULT_SECRET_ID}"
   ```
   (Pattern for all 7 services — use service-specific env var names.)

6. **Smoke test script** (`scripts/smoke-rotation.sh`):
   ```bash
   #!/usr/bin/env bash
   # Start all services, trigger a Vault DB credential rotation, verify health
   for svc in identity catalog orders payments content checkout-orchestrator notifications; do
     curl -sf "http://localhost:808x/health/ready" || (echo "FAIL: $svc" && exit 1)
   done
   echo "All services healthy after credential rotation"
   ```

---

## Acceptance

```bash
# All 7 services build:
dotnet build src/Identity/ src/Catalog/ src/Orders/ src/Payments/ \
             src/Content/ src/CheckoutOrchestrator/ src/Notifications/

# Integration smoke (requires local Vault + Postgres):
VAULT_ENABLED=true docker compose up -d
bash scripts/smoke-rotation.sh
```

---

## Anti-stuck

- The `VAULT_ENABLED=false` fallback is mandatory. Without it, the local dev loop (which runs without Vault) breaks for every developer.
- AppRole `ROLE_ID` and `SECRET_ID` for each service are written to `.env.local` by `deploy/vault/seed.sh` (or will be after R1). Read that file to understand the naming convention before setting env var names.
- Do not modify the Hangfire or MassTransit DI registration in any service — only the Npgsql/DbContext registration changes.
- bff-web has `has_db: false` in `services.json` — skip it entirely.
- If a service's `DependencyInjection.cs` uses `options.UseNpgsql(connectionString)` in `AddDbContext`, the migration must remain compatible — `VaultRotatingConnectionStringProvider` replaces the static connection string but the `DbContextOptions` must still use the rotating string at context creation time. Use `IConnectionStringProvider` injected into the `DbContextOptionsBuilder` factory.

---

## Done-report format

```
brief: R6
status: done | blocked
files_changed:
  - src/Identity/Identity.Infrastructure/DependencyInjection.cs
  - src/Catalog/Catalog.Infrastructure/DependencyInjection.cs
  - src/Orders/Orders.Infrastructure/DependencyInjection.cs
  - src/Payments/Payments.Infrastructure/DependencyInjection.cs
  - src/Content/Content.Infrastructure/DependencyInjection.cs
  - src/CheckoutOrchestrator/CheckoutOrchestrator.Infrastructure/DependencyInjection.cs
  - src/Notifications/Notifications.Infrastructure/DependencyInjection.cs
  - src/Notifications/.../Consumers/SecretExpiryWarningConsumer.cs
  - deploy/compose/docker-compose.yml  (env vars added)
  - scripts/smoke-rotation.sh
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
