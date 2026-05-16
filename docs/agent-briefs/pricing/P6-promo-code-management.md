# P6 — Promotion Code Management (Pessimistic Lock)

**Brief:** P6 | **Spec:** `docs/agent-briefs/pricing-tax-engine-spec.md`
**Phase:** 3 (parallel with P5 — both require P2+P3+P4 complete)
**Time budget:** 30 min

---

## Inputs

- `src/Pricing/Pricing.Domain/Entities/PromotionCode.cs` (from P1)
- `src/Pricing/Pricing.Infrastructure/Persistence/PricingDbContext.cs` (from P1)
- Spec §4.2 (PromotionCode domain model) and §5.1 step 9-12 (pessimistic lock protocol)

---

## Deliverable

### RedeemPromotionCodeCommand

`src/Pricing/Pricing.Application/Commands/RedeemPromotionCodeCommand.cs`

MediatR command:
```csharp
public sealed record RedeemPromotionCodeCommand : IRequest<RedeemPromotionCodeResult>
{
    public required string Code { get; init; }
    public required Guid UserId { get; init; }
    public required DateTimeOffset At { get; init; }
}

public sealed record RedeemPromotionCodeResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }  // "exhausted" | "expired" | "inactive"
    public PriceRule? PriceRule { get; init; }
}
```

Handler implementation (pessimistic lock):
```csharp
// 1. BEGIN TRANSACTION
// 2. SELECT FOR UPDATE on PromotionCodes row (500ms lock timeout)
//    EF: context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '500ms'")
//        then context.PromotionCodes
//            .FromSqlRaw("SELECT * FROM promotion_codes WHERE code = {0} FOR UPDATE", code)
// 3. Call CanRedeem(At) — if false, ROLLBACK, return FailureReason
// 4. code.Redemptions++ (EF update)
// 5. COMMIT
// 6. Return PriceRule from code.PriceRule navigation
```

### CreatePriceRuleCommand + CreatePromotionCodeCommand

Admin commands for seeding price rules and promo codes:
- `POST /admin/price-rules` — creates a `PriceRule`
- `POST /admin/promo-codes` — creates a `PromotionCode` linked to a `PriceRule`

Both require admin role claim. Return 201 with the created entity's ID.

---

## Acceptance

```bash
dotnet test tests/Pricing.Integration/ --filter "Category=PromoCode"
```

Integration tests (use `SharedTestPostgres.CreateDatabaseAsync("pricing")`):
- `RedeemPromotionCode_decrements_redemption_count_on_success`
- `RedeemPromotionCode_returns_exhausted_when_at_max_redemptions`
- `RedeemPromotionCode_returns_expired_when_past_expires_at`
- `RedeemPromotionCode_race_condition_two_concurrent_requests_only_one_succeeds`
  - Spin up 2 parallel tasks that both call the handler with a promo code where `MaxRedemptions=1`.
  - Assert exactly 1 succeeds and 1 returns `FailureReason = "exhausted"`.
  - Assert `PromotionCode.Redemptions == 1` in the DB.
  - This test must pass 10/10 times (run in a loop in the test).

---

## Anti-stuck

- PostgreSQL `FOR UPDATE` with `lock_timeout` is the correct mechanism. Do not use EF Core's optimistic concurrency (`[ConcurrencyCheck]`) for this — it will fail silently under high concurrency.
- `SET LOCAL lock_timeout = '500ms'` applies only to the current transaction. Use `ExecuteSqlRawAsync` before the `FromSqlRaw` call, within the same `BeginTransactionAsync` scope.
- If the lock times out, PostgreSQL throws error `55P03` (lock_not_available). Catch `NpgsqlException` with `SqlState == "55P03"` and return `FailureReason = "exhausted"` (conservative — the code might not actually be exhausted, but the safe behavior is to reject the redemption).
- Navigation property `code.PriceRule` must be eagerly loaded in the `FOR UPDATE` query (use `Include`). Lazy loading is not enabled.
- The race condition test requires `Isolation.ReadCommitted` (default). Do not change the isolation level.

---

## Done-report format

```
brief: P6
status: done | blocked
files_changed:
  - src/Pricing/Pricing.Application/Commands/RedeemPromotionCodeCommand.cs
  - src/Pricing/Pricing.Application/Commands/CreatePriceRuleCommand.cs
  - src/Pricing/Pricing.Application/Commands/CreatePromotionCodeCommand.cs
  - src/Pricing/Pricing.Api/Controllers/Admin/PriceRulesController.cs
  - src/Pricing/Pricing.Api/Controllers/Admin/PromoCodesController.cs
  - tests/Pricing.Integration/PromotionCodeRedemptionTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
