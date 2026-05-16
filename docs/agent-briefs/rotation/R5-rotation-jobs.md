# R5 — RotateJwtKeyJob + StripeKeyOverlapJob

**Brief:** R5 | **Spec:** `docs/agent-briefs/secret-rotation-spec.md`
**Phase:** 3 (parallel with R4 — both require R1 complete)
**Time budget:** 30 min

---

## Inputs

Read before touching anything:
- `src/Payments/Payments.Application/Commands/Subscriptions/CreateSubscriptionCheckoutCommand.cs` — for the payments command handler pattern to follow
- `src/Payments/Payments.Application/Interfaces/ICheckoutSessionService.cs` — interface style
- `infra/vault/secrets/kv-layout.json` — `payments/stripe` keys

---

## Deliverable

### 1. RotateJwtKeyJob (in scheduler-svc)

`src/Scheduler/Scheduler.Application/Jobs/RotateJwtKeyJob.cs`

30-day recurring Hangfire job:
1. Read current key from `secret/identity/jwt → Key` via Vault KV v2.
2. Copy it to `secret/identity/jwt-previous → Key` (create or overwrite).
3. Generate new key: `Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))`.
4. Write new key to `secret/identity/jwt → Key`.
5. Publish `JwtKeyRotatedEvent` via `IPublishEndpoint` (includes `RotationId = Guid.NewGuid()`, `RotatedAt = UtcNow`).
6. Schedule a Hangfire one-off job `ClearPreviousJwtKeyJob` to run in 15 minutes.

`ClearPreviousJwtKeyJob`:
- Deletes `secret/identity/jwt-previous` via Vault KV v2 delete.

Registration:
```csharp
RecurringJob.AddOrUpdate<RotateJwtKeyJob>(
    "rotate-jwt-key",
    job => job.RunAsync(CancellationToken.None),
    Cron.Monthly());   // first day of month, 02:00 UTC
```

### 2. StripeKeyRotationStartedEvent (`src/Contracts/Secrets/`)
```csharp
public sealed record StripeKeyRotationStartedEvent
{
    public required Guid RotationId { get; init; }
    public required DateTimeOffset OverlapExpiresAt { get; init; }
}
```

### 3. Admin endpoint on payments-svc

`src/Payments/Payments.Api/Controllers/Admin/StripeKeyRotationController.cs`

```
POST /admin/rotate-stripe-key
Authorization: Bearer <admin-token>   (requires "admin" role claim)
Body: { "newSecretKey": "sk_live_..." }

202 Accepted
{ "rotationId": "...", "overlapExpiresAt": "..." }
```

Handler (`RotateStripeKeyCommandHandler`):
1. Validate `newSecretKey` starts with `sk_live_` or `sk_test_` (reject others).
2. Read old key from `secret/payments/stripe → SecretKey`.
3. Write new key to `secret/payments/stripe → SecretKey`.
4. Publish `StripeKeyRotationStartedEvent`.
5. Schedule Hangfire one-off `RevokeOldStripeKeyJob` at `UtcNow + StripeKeyOverlapHours` (default 24h, configurable via `appsettings.json:Stripe:OverlapHours`).

`RevokeOldStripeKeyJob`:
- Calls Stripe API to revoke the old key (use `Stripe.ApiKeyService`).
- If Stripe API call fails, retry 3 times with exponential backoff, then log Error and alert ops via `SecretExpiryWarningEvent` with `AgePercent = 1.0`.

---

## Acceptance

```bash
dotnet build src/Payments/
dotnet build src/Scheduler/
dotnet test tests/Payments.Unit/     # admin endpoint handler unit tests
dotnet test tests/Scheduler.Unit/   # rotation job unit tests
```

Unit tests required:
- `RotateJwtKeyJob_writes_new_key_and_preserves_previous`
- `RotateJwtKeyJob_publishes_JwtKeyRotatedEvent`
- `RotateJwtKeyJob_schedules_ClearPreviousJwtKeyJob_in_15_minutes`
- `RotateStripeKeyCommandHandler_returns_202_with_rotation_id`
- `RotateStripeKeyCommandHandler_rejects_invalid_key_format`
- `RotateStripeKeyCommandHandler_schedules_revocation_job`

---

## Anti-stuck

- The admin endpoint requires an `admin` role claim. Follow the existing authorization pattern in payments-svc (look for `[Authorize(Roles = "...")]` in existing controllers).
- Do not store the old Stripe key in any event payload or log. Log only the `RotationId`.
- `StripeKeyOverlapHours` must be read from configuration, not hardcoded.
- `ClearPreviousJwtKeyJob` uses Vault KV v2 `DeleteSecretAsync` — this deletes the latest version, not all versions. If you want to delete all versions use `DeleteSecretPermanentlyAsync`. For this brief, `DeleteSecretAsync` (soft delete) is sufficient.

---

## Done-report format

```
brief: R5
status: done | blocked
files_changed:
  - src/Contracts/Secrets/StripeKeyRotationStartedEvent.cs
  - src/Scheduler/Scheduler.Application/Jobs/RotateJwtKeyJob.cs
  - src/Scheduler/Scheduler.Application/Jobs/ClearPreviousJwtKeyJob.cs
  - src/Scheduler/Scheduler.Application/Jobs/RevokeOldStripeKeyJob.cs
  - src/Payments/Payments.Application/Commands/Secrets/RotateStripeKeyCommand.cs
  - src/Payments/Payments.Api/Controllers/Admin/StripeKeyRotationController.cs
  - tests/Payments.Unit/RotateStripeKeyCommandHandlerTests.cs
  - tests/Scheduler.Unit/RotateJwtKeyJobTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
