# R4 — SecretExpiryWatcherJob (Hangfire)

**Brief:** R4 | **Spec:** `docs/agent-briefs/secret-rotation-spec.md`
**Phase:** 3 (parallel with R5 — both require R1 complete)
**Time budget:** 30 min

---

## Inputs

Read before touching anything:
- `src/Scheduler/Scheduler.Infrastructure/Persistence/HangfireEventScheduler.cs`
- `src/Scheduler/Scheduler.Infrastructure/DependencyInjection.cs`
- `src/Scheduler/Scheduler.Application/Common/Interfaces/IEventScheduler.cs`
- `src/Scheduler/Scheduler.Api/Controllers/SchedulingController.cs`
- `infra/vault/secrets/kv-layout.json` — all KV paths to monitor

---

## Deliverable

### 1. New event contract (`src/Contracts/Secrets/SecretExpiryWarningEvent.cs`)
```csharp
public sealed record SecretExpiryWarningEvent
{
    public required string SecretPath { get; init; }
    public required double AgePercent { get; init; }    // 0.0 to 1.0
    public required DateTimeOffset LastRotatedAt { get; init; }
}
```

### 2. SecretExpiryWatcherJob (`src/Scheduler/Scheduler.Application/Jobs/SecretExpiryWatcherJob.cs`)

Tracked secrets dictionary (hardcoded in v1, configurable in v2):
```csharp
private static readonly Dictionary<string, (TimeSpan TotalTtl, double WarnAt)> TrackedSecrets = new()
{
    ["secret/data/payments/stripe"]                          = (TimeSpan.FromDays(90),  0.80),
    ["secret/data/identity/jwt"]                             = (TimeSpan.FromDays(30),  0.80),
    ["secret/data/notifications/providers/sendgrid"]         = (TimeSpan.FromDays(365), 0.80),
    ["secret/data/notifications/providers/twilio"]           = (TimeSpan.FromDays(365), 0.80),
    ["secret/data/bff-web/hub"]                              = (TimeSpan.FromDays(365), 0.80),
};
```

Job logic:
1. For each tracked secret: call `VaultSharp` `ReadSecretMetadataAsync` (KV v2 metadata endpoint).
2. Read `CreatedTime` from metadata (this is the time the current version was written).
3. Compute `AgePercent = (UtcNow - CreatedTime) / TotalTtl`.
4. If `AgePercent >= WarnAt`: publish `SecretExpiryWarningEvent` via `IPublishEndpoint`.
5. On Vault unavailable (`VaultApiException`): log Warning, skip cycle, do not throw.

Decorate with `[AutomaticRetry(Attempts = 3)]`.

### 3. Registration in Scheduler.Infrastructure/DependencyInjection.cs

Add after `services.AddHangfireServer()`:
```csharp
services.AddScoped<SecretExpiryWatcherJob>();
```

And in `Scheduler.Api/Program.cs` (or a startup hook after the app builds):
```csharp
RecurringJob.AddOrUpdate<SecretExpiryWatcherJob>(
    "secret-expiry-watcher",
    job => job.RunAsync(CancellationToken.None),
    "*/15 * * * *");
```

---

## Acceptance

```bash
dotnet build src/Scheduler/
dotnet test tests/Scheduler.Unit/  # new tests must pass alongside existing
```

Unit tests required (add to `tests/Scheduler.Unit/` or create if absent):
- `SecretExpiryWatcherJob_publishes_warning_when_age_exceeds_threshold`
- `SecretExpiryWatcherJob_does_not_publish_when_below_threshold`
- `SecretExpiryWatcherJob_skips_cycle_when_vault_returns_503`
- `SecretExpiryWatcherJob_skips_cycle_when_vault_sealed`

---

## Anti-stuck

- KV v2 metadata endpoint path is `secret/metadata/{path}` (not `secret/data/{path}`). VaultSharp method: `V1.Secrets.KeyValue.V2.ReadSecretMetadataAsync(path, mountPoint: "secret")`.
- `IPublishEndpoint` is already available via MassTransit in scheduler-svc — reuse the existing registration.
- This job must be in `Scheduler.Application` (not Infrastructure) — it uses only Application-layer interfaces (`IPublishEndpoint`, `IVaultClient`).
- Do not add `IVaultClient` to the DI container in this brief — that is R6's responsibility. For this brief, inject `IVaultClient` and note it as a dependency that R6 must register.

---

## Done-report format

```
brief: R4
status: done | blocked
files_changed:
  - src/Contracts/Secrets/SecretExpiryWarningEvent.cs
  - src/Scheduler/Scheduler.Application/Jobs/SecretExpiryWatcherJob.cs
  - src/Scheduler/Scheduler.Infrastructure/DependencyInjection.cs
  - src/Scheduler/Scheduler.Api/Program.cs
  - tests/Scheduler.Unit/SecretExpiryWatcherJobTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
