# Secret Rotation — End-to-End Spec

**Status:** signed off 2026-05-16 — engine = HashiCorp Vault, scheduler = Hangfire (existing Scheduler service), Stripe rotation = automated via dual-key overlap
**Implementer:** Gemini CLI agents working brief-by-brief from `docs/agent-briefs/rotation/`
**Reviewer:** Claude / user, between phases
**Target:** zero-downtime automated rotation for all platform credentials; services reconnect within 30s; no restarts required

---

## 1. Goal & non-goals

**Goal.** Automated, zero-downtime rotation of every platform credential class:

- Postgres dynamic credentials (per-service Vault database roles, 24h TTL)
- Internal TLS certificates (Vault PKI engine, mTLS between services)
- Stripe API keys (monthly rolling via dual-key overlap)
- OAuth provider secrets, JWT signing keys, notification provider keys (KV v2 versioned rotation, monthly)

**Non-goals (v1):**

- External TLS certificates for public endpoints (those are Fly-managed; out of scope)
- Vault unseal automation / auto-unseal with cloud KMS (infra ops concern)
- Key derivation / envelope encryption at the application layer
- AWS IAM role federation (deferred; platform uses static AWS keys today)
- Secret scanning / SAST pipeline integration

---

## 2. Architecture at a glance

```
                         ┌─────────────────────────────────────────────┐
                         │  haworks-scheduler (existing Fly app)        │
                         │  ─────────────────────────────────────────  │
                         │  Scheduler.Api                               │
                         │  Scheduler.Application                       │
                         │  Scheduler.Infrastructure                    │
                         │    ├── HangfireEventScheduler (existing)     │
                         │    ├── LeaseWatcherJob         (NEW - R3)    │
                         │    └── RotationOrchestrator    (NEW - R3)    │
                         └──────────────┬──────────────────────────────┘
                                        │ HTTP (flycast)
                     ┌──────────────────▼──────────────────┐
                     │  haworks-vault (existing Fly app)    │
                     │  Raft storage, AppRole auth          │
                     │  ─────────────────────────────────  │
                     │  • KV v2     secret/*               │
                     │  • Database  database/*  (NEW - R1)  │
                     │  • PKI       pki/*        (NEW - R2)  │
                     └──────────────────────────────────────┘
                                        │
          ┌─────────────────────────────┼──────────────────────────────┐
          │                             │                              │
          ▼                             ▼                              ▼
  Per-service NpgsqlDataSource   PKI cert volume             KV v2 versioned
  (NpgsqlPeriodicPasswordProvider  (mounted at /certs)        secrets read at
   already wired in BuildingBlocks)  renewed by R4 extension   startup + IOptionsMonitor
                                                               refresh (R4)

                     ┌──────────────────────────────────────┐
                     │  RabbitMQ (existing)                  │
                     │  CredentialRotatedEvent (NEW - R3)    │
                     │  CertificateRotatedEvent (NEW - R3)   │
                     └──────────────────────────────────────┘
                                        │ consumed by
                         all services (BuildingBlocks extension R4)
```

**Why the Scheduler service (not a new service).** The Scheduler service already owns Hangfire with a dedicated Postgres backend, distributed locking semantics, and the `IEventScheduler` / `EventPublisherJob` pattern. Adding a `LeaseWatcherJob` is a pure additive brief — no new Fly app, no new DB, no new bootstrap.sh work.

**Why `IVaultService` is already 80% there.** `BuildingBlocks.Vault` already has `GetDatabaseCredentialsAsync`, `LeaseExpiryFor`, `LeaseDurationFor`, `RefreshCredentials`, and `GetKvSecretAsync`. The rotation layer is a thin orchestration wrapper on top; the heavy Vault API work is already tested.

**Why `NpgsqlDataSource.UsePeriodicPasswordProvider` (not custom pool drain).** `AddVaultNpgsqlDataSource` is already registered in `VaultServiceCollectionExtensions`. Npgsql's periodic provider transparently replaces the password used by new connections; old connections in the pool are evicted at their natural lifecycle point. Zero pool poisoning risk for the Postgres path.

---

## 3. Contracts

### 3.1 New domain events (in `src/Contracts/Rotation/`)

```csharp
// Published by LeaseWatcherJob after a successful Postgres credential rotation.
public sealed record CredentialRotatedEvent : DomainEvent
{
    public required string ServiceName { get; init; }   // e.g. "catalog"
    public required string RoleName    { get; init; }   // Vault DB role name
    public required string LeaseId     { get; init; }   // new lease ID (for audit)
    public required DateTimeOffset ExpiresAt { get; init; }
}

// Published after a PKI certificate is issued/renewed.
public sealed record CertificateRotatedEvent : DomainEvent
{
    public required string ServiceName  { get; init; }
    public required string CommonName   { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string SerialNumber { get; init; }
}

// Published after any KV secret is rotated (Stripe, JWT, etc.).
public sealed record KvSecretRotatedEvent : DomainEvent
{
    public required string SecretPath { get; init; }   // e.g. "payments/stripe"
    public required string KeyName    { get; init; }   // e.g. "SecretKey"
    public required int    NewVersion { get; init; }
}
```

### 3.2 BuildingBlocks extension — `IRotationNotifier`

```csharp
// Registered by R4's AddCredentialRefresh(). Each service subscribes to
// CredentialRotatedEvent for its own role and calls RefreshAsync.
public interface IRotationNotifier
{
    Task OnCredentialRotatedAsync(CredentialRotatedEvent evt, CancellationToken ct);
    Task OnCertificateRotatedAsync(CertificateRotatedEvent evt, CancellationToken ct);
    Task OnKvSecretRotatedAsync(KvSecretRotatedEvent evt, CancellationToken ct);
}
```

### 3.3 Vault policy names (per-service, added in R1/R2)

| Policy | Path | Capabilities |
|---|---|---|
| `rotation-worker` | `database/creds/*` | read |
| `rotation-worker` | `database/renew/*` | create, update |
| `rotation-worker` | `database/revoke/*` | create, update |
| `rotation-worker` | `pki/issue/*` | create, update |
| `rotation-worker` | `pki/revoke` | create, update |
| `rotation-worker` | `secret/data/*` | read |
| `<svc>-policy` | `database/creds/<svc>-role` | read |
| `<svc>-policy` | `pki/issue/<svc>-role` | create, update |

---

## 4. Vault database engine configuration (per service)

Each platform service gets its own Vault database role. The `database/` mount is configured once; roles are one-per-service.

**Services and their Vault role names:**

| Fly app | Vault role | Postgres DB | Max TTL |
|---|---|---|---|
| `haworks-catalog` | `catalog-role` | `haworks_catalog` | 24h |
| `haworks-orders` | `orders-role` | `haworks_orders` | 24h |
| `haworks-payments` | `payments-role` | `haworks_payments` | 24h |
| `haworks-checkout` | `checkout-role` | `haworks_checkout` | 24h |
| `haworks-identity` | `identity-role` | `haworks_identity` | 24h |
| `haworks-content` | `content-role` | `haworks_content` | 24h |
| `haworks-notifications` | `notifications-role` | `haworks_notifications` | 24h |
| `haworks-search` | `search-role` | `haworks_search` | 24h |
| `haworks-scheduler` | `scheduler-role` | `haworks_scheduler` | 24h |
| `haworks-webhooks` | `webhooks-role` | `haworks_webhooks` | 24h |

**Lease TTL policy:** default_ttl = 1h, max_ttl = 24h. The `LeaseWatcherJob` (R3) rotates at 80% of default_ttl (48 min) so credentials never expire mid-flight.

**Connection template** (applied uniformly):

```
{{username}}:{{password}}@<host>:5432/<db>?sslmode=require
```

SQL creation and revocation statements are the standard Vault Postgres templates (create role with LOGIN + VALID UNTIL, revoke on rotation).

---

## 5. Vault PKI engine configuration

**Internal CA hierarchy:**

```
Root CA (offline, stored in Vault PKI mount "pki")
  └── Intermediate CA (PKI mount "pki_int", 1 year TTL)
        ├── haworks-catalog.internal   (30d TTL)
        ├── haworks-orders.internal    (30d TTL)
        ├── haworks-payments.internal  (30d TTL)
        └── ... (one cert per service)
```

Certificates are SANs-scoped to `<svc>.internal` and `<svc>.flycast`. No wildcard certs.

**Mount points:** `pki` (root, offline), `pki_int` (intermediate, rotation worker writes here).

**Role per service:** `pki_int/roles/<svc>-role` with `allowed_domains = ["<svc>.internal", "<svc>.flycast"]`, `allow_subdomains = false`, `max_ttl = "720h"` (30d).

**Renewal trigger:** `LeaseWatcherJob` queries `pki_int/certs` list, reads each cert's `not_after`, renews any cert within 7 days of expiry.

---

## 6. Data model — `SchedulerDbContext` additions

The existing `SchedulerDbContext` gains two new tables (EF migration in R3):

```csharp
// Tracks active Vault leases the rotation worker is responsible for.
public class VaultLease
{
    public Guid   Id            { get; set; }
    public string ServiceName   { get; set; } = "";
    public string RoleName      { get; set; } = "";
    public string LeaseId       { get; set; } = "";
    public LeaseKind Kind        { get; set; }   // DbCredential | PkiCert | KvSecret
    public DateTimeOffset IssuedAt  { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public RotationStatus Status    { get; set; }  // Active | Rotating | Expired | Revoked
    public DateTimeOffset? LastRotatedAt { get; set; }
    public string? LastError { get; set; }
    public uint xmin { get; set; }   // Postgres xmin concurrency token (matches existing pattern)
}

public enum LeaseKind   { DbCredential, PkiCert, KvSecret }
public enum RotationStatus { Active, Rotating, Expired, Revoked, Failed }

// Audit log for every rotation attempt (immutable append-only).
public class RotationAuditEntry
{
    public Guid   Id          { get; set; }
    public Guid   LeaseId     { get; set; }
    public string ServiceName { get; set; } = "";
    public string RoleName    { get; set; } = "";
    public LeaseKind Kind     { get; set; }
    public bool   Succeeded   { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
```

Index: `VaultLease` on `(Status, ExpiresAt)` — the watcher job's primary query path.

---

## 7. Rotation job — LeaseWatcherJob

Hangfire recurring job, `Cron.Hourly()`, registered in `Scheduler.Infrastructure.DependencyInjection`:

```
RecurringJob.AddOrUpdate<LeaseWatcherJob>(
    "vault-lease-watcher",
    job => job.ExecuteAsync(CancellationToken.None),
    Cron.Hourly());
```

**Job execution steps:**

1. Query `VaultLease` where `Status == Active AND ExpiresAt < UtcNow + 80%TTLThreshold`. Batch size 50.
2. For each lease, check Vault `sys/leases/lookup/<leaseId>` — if Vault says it's already expired, mark `Expired` and skip.
3. Acquire a distributed lock via Hangfire's `DisableConcurrentExecution` attribute (one worker at a time globally — Hangfire's Postgres backend provides this).
4. Set `Status = Rotating` (optimistic concurrency via `xmin` — matches existing platform pattern).
5. Request new credentials from Vault:
   - `DbCredential` → `database/creds/<roleName>`
   - `PkiCert` → `pki_int/issue/<roleName>`
   - `KvSecret` → version bump (see R5 for Stripe-specific flow)
6. On success:
   - Update `VaultLease.LeaseId`, `ExpiresAt`, `Status = Active`, `LastRotatedAt = UtcNow`.
   - Append `RotationAuditEntry` (success).
   - Publish `CredentialRotatedEvent` / `CertificateRotatedEvent` via MassTransit outbox (same outbox pattern as `EventPublisherJob`).
7. On failure:
   - **Do not revoke the old credential.** Old credential remains valid.
   - Set `Status = Failed`, `LastError = ex.Message`.
   - Append `RotationAuditEntry` (failed).
   - Re-enqueue with exponential backoff (Hangfire retry attribute, 3 attempts, 5m/15m/60m).
   - After 3 failures, publish `RotationFailedAlert` (consumed by R6 alert consumer).
8. Token self-renewal: the rotation worker's own Vault token is renewed at the start of each job execution if `TokenTTLRemaining < TokenRenewalThresholdMinutes` (already wired in `VaultOptions.TokenRenewalThresholdMinutes`).

**Distributed locking:** `[DisableConcurrentExecution(timeoutInSeconds: 300)]` on `LeaseWatcherJob` ensures only one instance runs across all Scheduler replicas. Hangfire's PostgreSQL backend uses advisory locks. No custom Redis lock needed.

---

## 8. Application-level credential refresh (BuildingBlocks extension)

The goal is zero-restart credential refresh. The existing `AddVaultNpgsqlDataSource` already handles Postgres via `NpgsqlDataSource.UsePeriodicPasswordProvider` — this is the reference implementation. R4 extends the pattern to all credential types.

**IOptionsMonitor pattern for KV secrets:**

Services that consume KV secrets (Stripe key, JWT signing key, etc.) today read them once at startup via `IVaultService.GetKvSecretAsync`. R4 wraps these in `IOptionsMonitor<T>` so consumers get the current value on every call without restart:

```csharp
// R4 registers this; T is e.g. StripeOptions, JwtOptions.
public interface IVaultOptionsSource<T> : IOptionsChangeTokenSource<T>
{
    // Triggered when KvSecretRotatedEvent is received for this path.
    void SignalChange();
}
```

When `KvSecretRotatedEvent` arrives for `payments/stripe`, the `IVaultOptionsSource<StripeOptions>` signals `IOptionsMonitor<StripeOptions>`, which causes the next `monitor.CurrentValue` call to re-fetch from Vault. No service restart required.

**Certificate refresh:** `LeaseWatcherJob` writes renewed certificates to a shared volume path `/certs/<svc>/tls.crt` + `tls.key`. The R4 `CertificateRefreshHostedService` uses `FileSystemWatcher` on that path and calls `X509Certificate2.CreateFromPemFile` to rebuild the in-process `SslStreamFactory`. Existing TLS sessions are drained gracefully (Kestrel's `IConnectionLifetimeFeature.RequestClose`).

**Connection pool drain for Postgres:** Npgsql's `UsePeriodicPasswordProvider` issues new connections with the new password immediately. Old connections are evicted when they next idle-timeout (default 300s). If the old credential's Vault TTL is < 300s, the `LeaseWatcherJob` explicitly calls `NpgsqlConnection.ClearAllPools()` after publishing `CredentialRotatedEvent`. Consumers of the event in the affected service call `ClearAllPools()` on receipt.

---

## 9. Stripe key rotation (R5)

Stripe key rotation requires a dual-key overlap period because Stripe does not support atomic key swap. The flow:

1. `StripeKeyRotationJob` (Hangfire monthly recurring, `Cron.Monthly()`):
   a. Call Stripe API `POST /v1/restricted_keys` to create a new restricted key with identical permissions to the current key.
   b. Write new key to Vault `secret/data/payments/stripe` as a new KV v2 version (keep `SecretKey_pending` alongside `SecretKey`).
   c. Publish `KvSecretRotatedEvent` for `payments/stripe/SecretKey_pending`.
   d. Wait 60s (configurable via `StripeRotation:OverlapSeconds` — allows in-flight requests to complete).
   e. Verify new key works: call `GET /v1/balance` with new key, expect 200.
   f. On verification success:
      - Promote `SecretKey_pending` → `SecretKey` in Vault (new KV v2 version).
      - Publish `KvSecretRotatedEvent` for `payments/stripe/SecretKey`.
      - Call Stripe API to revoke the old key.
      - Remove `SecretKey_pending` from Vault (new version with key deleted).
   g. On verification failure:
      - Revoke the new key on Stripe.
      - Delete `SecretKey_pending` from Vault.
      - Publish `RotationFailedAlert`.
2. The `IVaultOptionsSource<StripeOptions>` signals on step (f) — payment-svc picks up the new key without restart.
3. **Race guard:** `StripeKeyRotationJob` uses `[DisableConcurrentExecution(timeoutInSeconds: 600)]`. Only one rotation runs at a time. If a Stripe charge arrives during the overlap window, the payment-svc MassTransit consumer retries on Stripe 402/401 (existing Polly policy) — retry will succeed once `SecretKey` is promoted.

---

## 10. Monitoring and alerts (R6)

### 10.1 Metrics

All exposed via OpenTelemetry (existing `BuildingBlocks.Telemetry` wiring):

| Metric | Type | Labels | Alert threshold |
|---|---|---|---|
| `vault_lease_ttl_ratio` | Gauge | `service`, `role`, `kind` | > 0.80 (80% of TTL elapsed without rotation) |
| `rotation_total` | Counter | `service`, `role`, `kind`, `result=success\|failure` | failure count > 0 in 5m window |
| `rotation_duration_ms` | Histogram | `service`, `role`, `kind` | p99 > 30s |
| `certificate_days_remaining` | Gauge | `service`, `cn` | < 7 days |
| `stripe_key_age_days` | Gauge | — | > 28 days |
| `vault_token_ttl_seconds` | Gauge | `service` | < 600s |

### 10.2 Alert consumers

A `RotationAlertConsumer` in `Scheduler.Application` subscribes to `RotationFailedAlert`. For v1 it writes a structured error log at `Critical` level which Fly's log drain forwards to whatever observability stack is configured. In v2, wire to `notifications-svc` (send to the ops Slack channel or PagerDuty).

### 10.3 Health check

`/health/ready` on `haworks-scheduler` gains a `VaultLeaseHealthCheck` that returns `Unhealthy` if any `VaultLease` has `Status == Failed` and `LastRotatedAt` is null (never successfully rotated) or `ExpiresAt < UtcNow + 2h`.

---

## 11. SLA targets

| Metric | Target | How measured |
|---|---|---|
| Postgres credential rotation | Every 24h, zero downtime | `RotationAuditEntry` success rate |
| Service reconnect after rotation | < 30s | `NpgsqlPeriodicPasswordProvider` interval = 5m, pool eviction = passive; manual `ClearAllPools()` on event bounds it to < 5s |
| Stripe key rotation | Monthly, < 5 min total overlap | `StripeKeyRotationJob` duration trace span |
| Alert on 80% TTL without rotation | < 1 min detection lag | `LeaseWatcherJob` runs hourly; gauge scrape interval = 30s |
| Certificate expiry alert | 7 days before expiry | `certificate_days_remaining` gauge < 7 |
| Vault token self-renewal | 10 min before expiry | `VaultOptions.TokenRenewalThresholdMinutes = 10` (existing) |

---

## 12. Failure modes

| Failure | Detection | Mitigation |
|---|---|---|
| Rotation fails mid-flight | `Status == Rotating` timeout (> 10m) | `LeaseWatcherJob` resets stale `Rotating` rows to `Failed` on next run; old credential still valid |
| Vault sealed during rotation | `VaultException` with 503 | Hangfire retry with exponential backoff (5m/15m/60m); `RotationFailedAlert` after 3 failures |
| Multiple Scheduler replicas attempt rotation simultaneously | Both try to CAS `xmin` on `VaultLease` | Second writer gets `DbUpdateConcurrencyException`, retries next poll cycle (Hangfire `DisableConcurrentExecution` prevents double-execution at job level) |
| NpgsqlDataSource pool poisoning | Old password connections fail after credential rotation | `NpgsqlConnection.ClearAllPools()` called on `CredentialRotatedEvent` receipt in affected service |
| Stripe key rotation race (charge during overlap) | Stripe 401 on in-flight charge | Polly retry (existing) retries charge after `SecretKey` is promoted; `SecretKey_pending` never exposed to payment flow |
| Certificate rotation during active TLS session | mTLS handshake failure on new connections during cert swap | Old cert kept valid by Vault until `pki_int/revoke` is called (not called until new cert is confirmed healthy); `CertificateRefreshHostedService` drains existing sessions before swapping |
| Rotation worker's Vault token expires | `VaultException` 403 | Token self-renewal runs at job start; `VaultTokenRevocationHostedService` (existing) handles graceful shutdown; if token expires mid-rotation, Hangfire retries the job which re-authenticates via AppRole |
| Lease record missing from DB (e.g. first deploy) | `VaultLease` table empty | `LeaseBootstrapStartupTask` (R3) seeds one row per service/role on Scheduler startup by calling `database/creds/<role>` for each registered role |

---

## 13. Topology & deployment

**No new Fly apps.** All rotation logic runs inside `haworks-scheduler` (existing). The only new Vault infrastructure is:
- `database/` secrets engine mount (enabled once via `vault-init.sh` additions in R1)
- `pki` + `pki_int` mounts (enabled once via `vault-init.sh` additions in R2)
- One new AppRole policy (`rotation-worker`) with the grants in §3.3

**`vault-init.sh` additions** (idempotent, wrapped in `vault secrets list | grep -q database || vault secrets enable` guards):
- R1: enable `database` mount, configure each Postgres connection, create per-service roles
- R2: enable `pki` + `pki_int` mounts, generate/sign intermediate CA, create per-service PKI roles

**`fly.vault-pg.toml`** — no changes; the Vault Postgres backend connection is separate from the application databases.

**`deploy.yml`** — no changes; `haworks-scheduler` is already in the deploy matrix.

**`infra/vault/secrets/kv-layout.json`** — add `rotation/stripe` path with `SecretKey`, `SecretKey_pending` keys (R5).

---

## 14. Test plan

Four layers per brief. All integration tests use `SharedTestPostgres.CreateDatabaseAsync("scheduler")` — never raw `PostgreSqlBuilder`.

### 14.1 Unit (`tests/Rotation.Unit/`)

- `LeaseWatcherJobTests` — TTL threshold logic; stale `Rotating` detection; `RotationAuditEntry` creation; retry enqueue on failure.
- `StripeKeyRotationJobTests` — dual-key state machine; verification-success path; verification-failure rollback; `ClearAllPools()` call after rotation.
- `VaultOptionsSourceTests` — `IOptionsMonitor` signal fired on `KvSecretRotatedEvent`; `CurrentValue` returns updated value after signal.
- `CertificateRefreshServiceTests` — `FileSystemWatcher` triggers cert reload; old sessions drained before swap; graceful fallback on corrupt cert file.
- Coverage target: > 90% on Application + Domain (excluding EF migrations).

### 14.2 Integration (`tests/Rotation.Integration/`)

- `RotationWebAppFactory` — mirrors `PaymentsWebAppFactory` shape; `SharedTestPostgres.CreateDatabaseAsync("scheduler")`; MassTransit test harness with all rotation consumers registered; Vault replaced by `IVaultService` mock (configurable per test).
- **Lease watcher tests:**
  - `LeaseWatcher_rotates_near_expiry_db_credential` — seed a `VaultLease` at 85% TTL; run job; assert `CredentialRotatedEvent` published and `Status == Active`.
  - `LeaseWatcher_does_not_rotate_healthy_lease` — seed a fresh lease; run job; assert no rotation.
  - `LeaseWatcher_marks_failed_and_retries_when_vault_errors` — mock Vault throws; assert `Status == Failed` after 3 retries.
  - `LeaseWatcher_resets_stale_rotating_row` — seed `Status == Rotating` with `LastRotatedAt` 15m ago; run job; assert reset to `Failed`.
- **Stripe rotation tests:**
  - `StripeJob_creates_pending_key_then_promotes_on_verify_success`
  - `StripeJob_revokes_pending_key_on_verify_failure`
  - `StripeJob_does_not_run_concurrently`
- **IOptionsMonitor tests:**
  - `OptionsMonitor_reflects_new_stripe_key_without_service_restart`
  - `OptionsMonitor_reflects_new_jwt_key_without_service_restart`

### 14.3 End-to-end (`tests/Rotation.E2E/`)

Runs against a local Vault dev server (started by the test harness via `SharedTestVault` singleton — follows same pattern as `SharedTestPostgres`):

- `DbCredential_rotates_and_service_reconnects_within_30s` — full Vault + Postgres stack; trigger rotation; assert new connection succeeds before 30s timeout.
- `Certificate_rotates_and_new_handshake_succeeds` — issue cert via `pki_int`; rotate; assert new `X509Certificate2` is loaded without process restart.

### 14.4 Smoke (`tests/Smoke/`)

One assertion appended: `GET <scheduler>/health/ready` returns 200 with `VaultLeaseHealthCheck` healthy post-deploy.

---

## 15. Implementation plan (Gemini CLI agents)

Six self-contained briefs in `docs/agent-briefs/rotation/`. Each brief is one Gemini CLI invocation. **Hard checkpoints between phases.**

```
Phase 1: Vault engine setup (infrastructure, no .NET code yet)
  R1  vault-init.sh: enable database secrets engine, configure 10 Postgres
      connections, create per-service roles with creation_statements,
      TTL policies. Idempotent. Test: `vault read database/creds/catalog-role`
      returns a username.
      kv-layout.json: add rotation/stripe path.
      → CHECKPOINT: `vault read database/creds/<each-role>` returns creds for
        all 10 services. `vault policy read rotation-worker` matches §3.3.

  R2  vault-init.sh: enable pki + pki_int mounts, generate root CA, sign
      intermediate, create per-service PKI roles with correct allowed_domains.
      fly.vault.toml: expose port 8201 (cluster — not strictly needed for
      single-node but documents the upgrade path).
      → CHECKPOINT: `vault write pki_int/issue/catalog-role common_name=
        haworks-catalog.internal` returns a certificate. `openssl verify`
        against the root CA passes.

Phase 2: Scheduler rotation job (blocks on R1 conceptually; can write
          code in parallel since vault-init.sh is infra-only)
  R3  Scheduler-side rotation worker:
      - New domain events in src/Contracts/Rotation/ (3 records).
      - EF migration on SchedulerDbContext: VaultLease + RotationAuditEntry tables.
      - LeaseWatcherJob (Hangfire recurring, hourly).
      - RotationOrchestrator (Application service called by job).
      - LeaseBootstrapStartupTask (seeds VaultLease rows on startup).
      - StripeKeyRotationJob (monthly recurring).
      - Wire in Scheduler.Infrastructure.DependencyInjection.
      - Unit tests in tests/Rotation.Unit/.
      → CHECKPOINT: dotnet build clean; unit tests green; migration applies
        cleanly against SharedTestPostgres.

Phase 3: Application-level refresh (depends on R3 events existing)
  R4  BuildingBlocks.Vault additions:
      - IVaultOptionsSource<T> + VaultOptionsSource<T> implementation.
      - AddCredentialRefresh<T>(path, key) extension method.
      - CertificateRefreshHostedService (FileSystemWatcher + graceful drain).
      - IRotationNotifier + consumer registration helper.
      - Wire into AddVaultIntegration().
      - Unit + integration tests in tests/Rotation.Unit/ and
        tests/Rotation.Integration/.
      → CHECKPOINT: IOptionsMonitor<StripeOptions>.CurrentValue returns
        updated value after KvSecretRotatedEvent is published in test harness
        without restarting the host.

Phase 4: Stripe rotation (depends on R3 + R4)
  R5  StripeKeyRotationJob full implementation:
      - Stripe RestrictedKey API client (typed Refit interface in
        Payments.Infrastructure or BuildingBlocks — check where the existing
        StripeClient lives first).
      - Dual-key state machine (pending → verify → promote → revoke).
      - VaultLease row management for KvSecret kind.
      - Integration tests against WireMock Stripe stub.
      → CHECKPOINT: StripeJob_creates_pending_key_then_promotes_on_verify_success
        and _failure tests green.

Phase 5: Monitoring (depends on R3 for event types)
  R6  Monitoring + alerts:
      - OTel metrics registration (all gauges/counters in §10.1) in
        Scheduler.Infrastructure.
      - VaultLeaseHealthCheck registered in Scheduler.Api/Program.cs.
      - RotationAlertConsumer in Scheduler.Application.
      - Unit test: health check returns Unhealthy when any lease is Failed.
      → CHECKPOINT: dotnet test green; `dotnet-counters monitor` shows
        vault_lease_ttl_ratio gauge when running locally.

Phase 6: E2E + smoke (depends on all above)
  (E2E tests against local Vault dev — written when all briefs are in user's
   hands and the full stack can be stood up locally.)
```

**Anti-stuck rules baked into every brief:**

1. Read the **Inputs** section before writing any code. Don't grep the codebase blindly.
2. Stay inside the **Deliverable** scope. If you see a tempting refactor, **don't do it** — note it in the done-report under "out-of-scope observations".
3. `IVaultService` already exists in `src/BuildingBlocks/Vault/` — read it before implementing anything that calls Vault. Do not re-implement the Vault HTTP client.
4. `NpgsqlDataSource.UsePeriodicPasswordProvider` is already wired in `AddVaultNpgsqlDataSource` — do not add a second password rotation mechanism for Postgres. R4 handles KV secrets only.
5. Integration tests must use `SharedTestPostgres.CreateDatabaseAsync("scheduler")` — never `new PostgreSqlBuilder()`. CI will fail on raw container usage.
6. **Acceptance** commands are non-negotiable. If they don't pass, you're not done. File a `blocker:` and stop.
7. Hard time budget per brief: ~30 min of agent time. If stuck past 30 min, emit a blocker.
8. **No cross-brief edits.** R4 must not modify `LeaseWatcherJob`; that's R3's territory.
9. Done-report format per `docs/agent-briefs/audit-protocol.md`. Stick to it.

---

## 16. Sign-off (2026-05-16)

| Question | Decision |
|---|---|
| Rotation host | **Existing `haworks-scheduler`** — no new service |
| Postgres rotation mechanism | **Vault database engine** + existing `NpgsqlDataSource.UsePeriodicPasswordProvider` |
| PKI / mTLS | **Vault pki_int** — internal only; Fly TLS for public endpoints unchanged |
| Stripe rotation | **Automated monthly** via dual-key overlap; `StripeKeyRotationJob` |
| IOptionsMonitor refresh | **Yes** — KV secrets refreshed without restart via `IVaultOptionsSource<T>` |
| Connection pool drain | **`ClearAllPools()` on `CredentialRotatedEvent`** — bounded to < 5s |
| Distributed lock | **Hangfire `DisableConcurrentExecution`** — no Redis lock needed |
| Alert delivery (v1) | **Structured log at Critical level** — Fly log drain; Slack/PagerDuty in v2 |
| Implementer | Gemini CLI agents, brief-by-brief |
| Reviewer | Claude / user, between phases |
