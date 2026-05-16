# Pricing & Tax Engine — End-to-End Spec

**Status:** signed off 2026-05-16 — engine = in-process rule evaluation, tax = configurable rates v1, Avalara = deferred to v2
**Implementer:** Gemini CLI agents working brief-by-brief from `docs/agent-briefs/pricing/`
**Reviewer:** Claude / user, between phases
**Target:** ship v1 with pricing plugged into CheckoutSaga before payment session creation; admin CRUD for rules + promotions

---

## 1. Goal & non-goals

**Goal.** Implement the `pricing-svc` microservice (the `src/Pricing/` scaffold already exists as a skeleton) to:

- Calculate an **effective price** for any product+quantity+promotion combination at checkout time.
- Support **tiered volume pricing**, **time-limited promotions**, and **percentage/fixed discounts**.
- Calculate **tax** using configurable per-country/state rates (v1 in-process; v2 adapter for Avalara/TaxJar).
- Integrate into **CheckoutSaga** between the `StockReserved` transition and `PaymentSessionRequestedEvent` publication, replacing the saga's current pass-through of `TotalAmount` with a pricing-svc-calculated breakdown.
- Persist a **calculation audit log** so every price decision is traceable and disputable.
- Expose **admin endpoints** for managing pricing rules, discount schedules, and promotion campaigns.

**Non-goals (v1):**
- Multi-currency (domain model must not preclude it; v1 is USD only)
- Avalara / TaxJar live API calls (adapter interface is specified; the stub implementation is what ships in v1)
- B2C subscription pricing (covered separately in the payments service)
- Bulk pricing import via CSV
- Shopper-facing coupon UI (BFF integration is out-of-scope; the endpoint exists, the BFF wires it in a future brief)
- A/B test pricing experimentation

---

## 2. Architecture at a glance

```
   checkout-orchestrator  ─── HTTP (flycast) ────►  pricing-svc
                                                      ─────────────────────────────
                                                      Pricing.Api
                                                       └─ PricingController
                                                       └─ TaxController
                                                       └─ AdminController (auth-gated)
                                                      Pricing.Application
                                                       └─ CalculateEffectivePriceQuery
                                                       └─ ApplyPromotionCodeCommand
                                                       └─ ValidatePromotionCodeQuery
                                                       └─ CreatePriceRuleCommand
                                                       └─ TaxCalculationService (in-proc v1)
                                                      Pricing.Infrastructure
                                                       └─ PricingDbContext (EF Core / Postgres)
                                                       └─ PriceRuleRepository
                                                       └─ PromotionCodeRepository
                                                       └─ TaxRateRepository
                                                       └─ CalculationLogRepository
                                                      Pricing.Domain
                                                       └─ PriceRule (aggregate)
                                                       └─ Discount (entity)
                                                       └─ TieredPrice (value object)
                                                       └─ PromotionCode (aggregate)
                                                       └─ TaxRate (entity)
                                                       └─ PriceCalculationResult (VO)
                                                      ─────────────────────────────
                                                              │ EF Core
                                                              ▼
                                                       haworks-pricing (Postgres / Neon)

   catalog-svc ──────────────────────────────────────────────────────────────────────
     Product.UnitPrice  ──── read via Catalog HTTP client ──►  used as BasePrice input
```

**Why pricing is its own service (not in catalog or checkout).** Three reasons:
1. Pricing rules are a first-class business domain with their own lifecycle (admin creates, schedules, expires rules independent of product catalog edits).
2. The calculation engine needs its own DB (rule tables, promotion redemption counters, audit log) — mixing it into catalog violates DB-per-service.
3. CheckoutSaga must call pricing after stock reservation and before payment — a dedicated service with a stable HTTP contract is the cleanest seam.

**Why in-process tax for v1.** External tax APIs (Avalara, TaxJar) require account setup and introduce an external failure mode. The in-process rate table covers 100% of the near-term product requirement. The `ITaxCalculator` interface means switching to an external adapter is a one-class change.

---

## 3. Contracts

### 3.1 HTTP — Pricing API

```
GET /pricing/calculate?productId={guid}&quantity={int}&promoCode={string?}&userId={string?}&countryCode={string?}&stateCode={string?}
   → 200 OK  (PriceBreakdownResponse)
   → 400 if productId missing, quantity < 1, quantity > 9999
   → 404 if productId has no pricing record (caller should fall back to catalog UnitPrice)
   → 422 if promoCode exists but is invalid/expired/exhausted
```

**Response:**

```jsonc
{
  "productId":       "9e9d…",
  "quantity":        3,
  "currency":        "USD",
  "baseUnitPrice":   29.9900,
  "effectiveUnitPrice": 24.9900,          // after volume/tiered discounts
  "discounts": [
    {
      "type":        "TieredVolume",
      "label":       "Buy 3+ get 10% off",
      "amountOff":   3.0000,              // per-unit reduction
      "pct":         10.00
    },
    {
      "type":        "PromotionCode",
      "label":       "WELCOME20",
      "amountOff":   4.9980,              // total, not per-unit
      "pct":         null
    }
  ],
  "subtotal":        74.9700,             // effectiveUnitPrice × qty minus promo
  "taxAmount":        6.3723,             // calculated at subtotal
  "taxRate":          0.0850,             // 8.5% (state+local)
  "total":           81.3423,
  "promoCodeApplied": "WELCOME20",
  "calculationId":   "a1b2…",            // persisted audit log id
  "snapshotAt":      "2026-05-16T14:22:00Z"
}
```

```
POST /pricing/promotions/validate
Body: { "code": "WELCOME20", "productId": "...", "userId": "..." }
→ 200 { "valid": true, "discountType": "Percentage", "value": 20.0, "expiresAt": "..." }
→ 200 { "valid": false, "reason": "Exhausted" }
→ 400 if code or productId missing
```

```
POST /pricing/promotions/redeem
Body: { "code": "WELCOME20", "productId": "...", "orderId": "...", "userId": "...", "calculationId": "..." }
→ 204 No Content (idempotent by orderId — safe to call twice)
→ 409 Conflict if redemption would exceed MaxUses
→ 422 if code expired or not yet active
```

**Health.** `GET /health` — 200 when DB ping succeeds. `/health/ready` includes DB. `/health/live` is process-up.

### 3.2 HTTP — Tax API (internal use only)

```
GET /pricing/tax/rate?countryCode={string}&stateCode={string?}
→ 200 { "countryCode": "US", "stateCode": "CA", "combinedRate": 0.0925, "breakdown": { "state": 0.0725, "county": 0.0100, "local": 0.0100 } }
→ 404 if no rate configured for that jurisdiction
```

### 3.3 HTTP — Admin API (requires `admin` role claim)

```
POST   /admin/pricing/rules                   → 201 Created
GET    /admin/pricing/rules?productId=&page=  → 200 paged
GET    /admin/pricing/rules/{id}              → 200
PUT    /admin/pricing/rules/{id}              → 204
DELETE /admin/pricing/rules/{id}              → 204

POST   /admin/pricing/promotions              → 201 Created
GET    /admin/pricing/promotions?page=        → 200 paged
GET    /admin/pricing/promotions/{id}         → 200
PUT    /admin/pricing/promotions/{id}         → 204
DELETE /admin/pricing/promotions/{id}         → 204 (soft-delete; in-flight redemptions are honoured)

GET    /admin/pricing/tax/rates               → 200 full rate table
PUT    /admin/pricing/tax/rates/{id}          → 204
POST   /admin/pricing/tax/rates               → 201
```

### 3.4 Inbound events (consumed from RabbitMQ)

| Event | Source | Action |
|---|---|---|
| `ProductCacheInvalidatedEvent` | catalog (existing) | Invalidate any cached `baseUnitPrice` for that productId in the pricing engine |

### 3.5 Outbound events (published to RabbitMQ via outbox)

| Event | Trigger | Consumer |
|---|---|---|
| `PriceCalculatedEvent` | Every `CalculateEffectivePriceQuery` | Analytics, audit sidecar |
| `PromotionRedeemedEvent` | Every successful `/promotions/redeem` | Notifications (congrats email), Analytics |

New contract records go in `src/Contracts/Pricing/`.

---

## 4. Domain model

### 4.1 PriceRule aggregate

```
PriceRule
  Id                   Guid
  ProductId            Guid?          // null = applies to all products in scope
  CategoryId           Guid?          // null = applies globally
  Priority             int            // higher = evaluated first; ties broken by specificity
  DiscountType         enum           // Percentage | FixedAmount | FreeShipping
  DiscountValue        decimal(18,4)  // 10 = "10%" or "$10.00" depending on type
  MinimumQuantity      int            // 0 = always applies
  MaximumQuantity      int?           // null = no upper bound
  StartsAt             DateTimeOffset?
  ExpiresAt            DateTimeOffset?
  SellerTimezone       string         // IANA tz, e.g. "America/New_York" — StartsAt/ExpiresAt evaluated in this tz
  IsActive             bool
  CreatedAt            DateTimeOffset
  UpdatedAt            DateTimeOffset

  // Navigation
  TieredPrices         IReadOnlyList<TieredPrice>
```

**Invariants (enforced by factory method `PriceRule.Create`):**
- `DiscountValue` must be > 0.
- For `Percentage` type: `DiscountValue` must be ≤ 100.
- `MinimumQuantity` ≥ 0; if `MaximumQuantity` is set, it must be > `MinimumQuantity`.
- `ExpiresAt` must be null or > `StartsAt` (when `StartsAt` is set).
- `ProductId` and `CategoryId` cannot both be null (too broad for v1; platform-wide discounts are a v2 concern).

### 4.2 TieredPrice value object

```
TieredPrice  (owned by PriceRule)
  FromQuantity         int            // inclusive
  ToQuantity           int?           // inclusive; null = open-ended
  UnitPrice            decimal(18,4)  // absolute price at this tier (overrides rule's DiscountValue for tiered rules)
```

Tiers must not overlap. Validated at aggregate level in `PriceRule.AddTier(...)`.

### 4.3 PromotionCode aggregate

```
PromotionCode
  Id                   Guid
  Code                 string         // unique, case-insensitive, max 32 chars, [A-Z0-9_-] only
  DiscountType         enum           // Percentage | FixedAmount
  DiscountValue        decimal(18,4)
  MinimumOrderAmount   decimal(18,4)? // null = no minimum
  ApplicableProductId  Guid?          // null = any product
  ApplicableCategoryId Guid?          // null = any category
  MaxUses              int?           // null = unlimited
  UsesCount            int            // incremented atomically via SQL UPDATE ... WHERE UsesCount < MaxUses
  MaxUsesPerUser       int?           // null = unlimited
  StartsAt             DateTimeOffset?
  ExpiresAt            DateTimeOffset?
  SellerTimezone       string
  IsActive             bool
  CreatedAt            DateTimeOffset
  UpdatedAt            DateTimeOffset

  // Navigation
  Redemptions          IReadOnlyList<PromotionRedemption>
```

**Invariants:**
- `Code` stored uppercase; `ValidatePromotionCodeQuery` normalizes input before lookup.
- `DiscountValue` > 0; for `Percentage`, ≤ 100.
- `MaxUsesPerUser` requires that `userId` is captured on redemption.

### 4.4 PromotionRedemption entity

```
PromotionRedemption   (child of PromotionCode)
  Id                   Guid
  PromotionCodeId      Guid
  OrderId              Guid           // idempotency key: one redemption per order
  UserId               string?
  RedeemedAt           DateTimeOffset
  DiscountAmountApplied decimal(18,4) // actual dollars off, captured at redemption time
```

### 4.5 TaxRate entity

```
TaxRate
  Id                   Guid
  CountryCode          string         // ISO 3166-1 alpha-2 ("US", "GB", "CA")
  StateCode            string?        // ISO 3166-2 subdivision code ("CA", "NY"); null = country-level rate
  CombinedRate         decimal(8,6)   // e.g. 0.092500 = 9.25%
  StateRate            decimal(8,6)
  CountyRate           decimal(8,6)
  LocalRate            decimal(8,6)
  EffectiveFrom        DateTimeOffset
  EffectiveTo          DateTimeOffset?
  Notes                string?
```

Seed data: US state rates from 2026 schedule (see §P7). UK, CA, AU added as flat country-level rates.

### 4.6 PriceCalculationLog entity

```
PriceCalculationLog
  Id                   Guid
  ProductId            Guid
  Quantity             int
  BaseUnitPrice        decimal(18,4)
  EffectiveUnitPrice   decimal(18,4)
  Subtotal             decimal(18,4)
  TaxAmount            decimal(18,4)
  TaxRate              decimal(8,6)
  Total                decimal(18,4)
  Currency             string
  AppliedRuleIds       string         // JSON array of PriceRule IDs applied
  PromotionCodeApplied string?
  CalculatedAt         DateTimeOffset
  UserId               string?        // null for anonymous
  CountryCode          string?
  StateCode            string?
  SnapshotProductPrice decimal(18,4)  // catalog UnitPrice at calculation time
```

Logs are **append-only**. Never updated. Retained for 2 years (add index on `CalculatedAt`).

---

## 5. Calculation engine

### 5.1 Rule evaluation pipeline

`CalculateEffectivePriceQuery` → `CalculateEffectivePriceQueryHandler`:

```
1. Fetch BaseUnitPrice from catalog HTTP client (GET /api/products/{id}, read UnitPrice).
   Cache result in-memory for 60s (IMemoryCache) keyed by productId to reduce catalog chattiness.

2. Load active PriceRules matching:
     (ProductId == productId OR CategoryId == product.CategoryId OR (ProductId IS NULL AND CategoryId IS NULL))
     AND IsActive == true
     AND (StartsAt IS NULL OR StartsAt <= NOW_IN_SELLER_TZ)
     AND (ExpiresAt IS NULL OR ExpiresAt > NOW_IN_SELLER_TZ)
     AND (MinimumQuantity <= quantity)
     AND (MaximumQuantity IS NULL OR MaximumQuantity >= quantity)
   Order by Priority DESC, specificity (ProductId match > CategoryId match > global).

3. Check TieredPrices first (if the applicable PriceRule has tiers):
   - Find the tier where FromQuantity <= quantity <= ToQuantity (or open-ended).
   - If found, EffectiveUnitPrice = tier.UnitPrice (absolute override).
   - Remaining discount rules are still applied on top of the tiered price.

4. Apply remaining PriceRules in priority order:
   - Percentage discount: EffectiveUnitPrice *= (1 - DiscountValue/100)
   - FixedAmount discount: EffectiveUnitPrice -= DiscountValue
   - Floor at zero: EffectiveUnitPrice = max(EffectiveUnitPrice, 0)
   - Also floor at product's MinimumPrice if set (v1: no MinimumPrice field; floor is zero)

5. Apply PromotionCode (if provided):
   a. Validate (not expired, not exhausted, applicable to this product/category).
   b. Percentage: Subtotal after tiered/rule discounts *= (1 - code.DiscountValue/100)
      FixedAmount: Subtotal -= code.DiscountValue (floor at 0)
   c. Promotion is applied to the subtotal (quantity × effectiveUnitPrice), not per-unit.

6. Calculate Subtotal = EffectiveUnitPrice × Quantity (then apply promotion discount).

7. Calculate Tax:
   - Call ITaxCalculator.CalculateAsync(countryCode, stateCode, subtotal)
   - v1 implementation: look up TaxRate table, multiply combinedRate × subtotal.
   - If countryCode is null: TaxAmount = 0, TaxRate = 0 (no jurisdiction → no tax).
   - Tax calculation failure (DB error, rate not found): fail-closed → return 500.
     DO NOT silently zero tax and pass a lower total to the payment provider.

8. Total = Subtotal + TaxAmount.

9. Persist PriceCalculationLog (append-only insert, committed in the same transaction as outbox event).

10. Return PriceBreakdownResponse including calculationId.
```

### 5.2 Determinism guarantee

- All arithmetic uses `decimal` (never `float`/`double`).
- Rounding rule: `Math.Round(..., 4, MidpointRounding.AwayFromZero)` at each step.
- When converting to cents for Stripe: `(long)Math.Round(total * 100, 0, MidpointRounding.AwayFromZero)`.
- Same `calculationId` can be replayed: given same inputs + same rule state, output is identical. (Rule state is immutable once applied — rules are soft-deleted, never mutated after use.)

### 5.3 Price snapshot in CheckoutSaga

The saga stores `TotalAmount` on the `CheckoutSagaState`. After pricing integration (P5), the saga will store:

```
PricingCalculationId   Guid?          // null until pricing step completes
PricedSubtotal         decimal        // subtotal from pricing-svc response
PricedTaxAmount        decimal
PricedTotal            decimal        // = PricedSubtotal + PricedTaxAmount
```

Once captured at the `StockReserved` → `PricingCompleted` transition, these values are **immutable for the lifetime of that saga**. Even if the admin changes a PriceRule mid-checkout, the saga uses the snapshotted amounts. The payment provider is charged `PricedTotal`, not a re-calculated value.

---

## 6. Tax calculation module

### 6.1 Interface

```csharp
// Pricing.Application/Interfaces/ITaxCalculator.cs
public interface ITaxCalculator
{
    /// <summary>
    /// Returns the tax amount for the given subtotal in the specified jurisdiction.
    /// Throws TaxCalculationException (which becomes HTTP 500) if the rate cannot be determined.
    /// Never returns a silent zero for a known taxable jurisdiction.
    /// </summary>
    Task<TaxCalculationResult> CalculateAsync(
        string?  countryCode,
        string?  stateCode,
        decimal  subtotal,
        string   currency,
        CancellationToken ct = default);
}

public sealed record TaxCalculationResult(
    decimal TaxAmount,
    decimal EffectiveRate,
    string  Source          // "RateTable" | "Avalara" | "None" (for null jurisdiction)
);
```

### 6.2 v1 Implementation: `RateTableTaxCalculator`

```csharp
// Pricing.Infrastructure/Tax/RateTableTaxCalculator.cs
// - Loads TaxRate from PricingDbContext where CountryCode + StateCode match,
//   EffectiveFrom <= now, EffectiveTo IS NULL OR EffectiveTo > now.
// - Takes the most-specific match (StateCode wins over country-only).
// - If no row found for a non-null jurisdiction: throw TaxCalculationException
//   ("No tax rate configured for {countryCode}/{stateCode}").
// - TaxAmount = Math.Round(rate.CombinedRate * subtotal, 4, MidpointRounding.AwayFromZero).
```

### 6.3 v2 Adapter slot: `AvalaraTaxCalculator`

```csharp
// Pricing.Infrastructure/Tax/AvalaraTaxCalculator.cs  — stub only in v1
// Registered via feature flag: if Pricing:TaxProvider == "Avalara"
// Register AvalaraTaxCalculator instead of RateTableTaxCalculator.
// The interface contract is identical — swap is zero-callsite-change.
```

### 6.4 Failure policy

**Fail-closed.** If `ITaxCalculator.CalculateAsync` throws, the `CalculateEffectivePriceQuery` handler throws `TaxCalculationException`, which the API maps to HTTP 500. CheckoutSaga receives a `PricingFailedEvent` and compensates by releasing stock (same path as `PaymentSessionFailedEvent`). The customer sees "checkout unavailable, try again." This is the correct behaviour: sending a payment to Stripe for less than the correct total (because we zeroed tax) is a revenue leak that cannot be corrected later.

---

## 7. Promotion code race conditions

Atomic redemption uses a **compare-and-swap UPDATE**:

```sql
UPDATE "PromotionCodes"
SET "UsesCount" = "UsesCount" + 1, "UpdatedAt" = NOW()
WHERE "Id" = @id
  AND ("MaxUses" IS NULL OR "UsesCount" < "MaxUses")
RETURNING "UsesCount";
```

If zero rows are affected, the redemption is rejected with HTTP 409. This is executed inside a serializable transaction. EF Core does not generate this SQL natively — use `ExecuteSqlRawAsync` in `PromotionCodeRepository.TryRedeemAsync(...)`. The `UsesCount` column has a `CHECK (UsesCount >= 0)` constraint so a concurrent decrement bug cannot produce negative counts.

**Per-user limit enforcement:** before attempting the CAS UPDATE, check:
```sql
SELECT COUNT(*) FROM "PromotionRedemptions"
WHERE "PromotionCodeId" = @id AND "UserId" = @userId
```
If count >= `MaxUsesPerUser`, return 409 before the CAS. This is a pre-check, not a serializable guarantee — the edge case (two simultaneous requests for the same user) is acceptable; the worst outcome is one extra redemption if two requests land in the same millisecond.

---

## 8. Timezone-sensitive promotions

`StartsAt` and `ExpiresAt` on `PriceRule` and `PromotionCode` are stored as `DateTimeOffset` (UTC). The `SellerTimezone` field (IANA zone, e.g. `"America/New_York"`) is used only to communicate intent to the admin UI and for display purposes. The actual effective window is expressed as UTC offsets by the admin tool (or a future admin UI) at creation time. The engine compares against `DateTimeOffset.UtcNow` directly.

**Rationale:** Storing "Black Friday starts at midnight seller-local-time" as the UTC equivalent (e.g. `2026-11-27T05:00:00Z` for US Eastern) is simpler and safer than doing timezone math in the query. The admin API accepts `startsAt` as an ISO 8601 string with timezone offset (`"2026-11-27T00:00:00-05:00"`) and stores the UTC equivalent. The `SellerTimezone` is stored for audit/display only.

**In-flight promotion expiry:** If a promotion expires between `ValidatePromotionCodeQuery` and `RedeemPromotionCodeCommand`, the redemption endpoint re-validates expiry atomically. Honour-if-applied-before-expiry rule: the `CalculateEffectivePriceQuery` captures `snapshotAt` in the `PriceCalculationLog`. At redemption time, `snapshotAt` is checked against `ExpiresAt` — if `snapshotAt < ExpiresAt`, the redemption proceeds even if `now > ExpiresAt`.

---

## 9. SLA targets

| Metric | Target | How measured |
|---|---|---|
| `GET /pricing/calculate` p50 (internal flycast) | < 15 ms | trace span |
| `GET /pricing/calculate` p99 | < 60 ms | trace span |
| Promotion redemption CAS | < 20 ms (excludes caller latency) | trace span |
| Tax rate lookup p99 | < 5 ms | DB query span |
| Checkout saga added latency (pricing step) | < 100 ms net | saga trace |
| Availability | 99.9% / 30d | uptime check |

---

## 10. Topology & deployment

**One new Fly app.** `haworks-pricing` — stateless, `shared-cpu-1x` 256 MB, region `iad`. Internal only (flycast). One Neon database `haworks-pricing` added to `bootstrap.sh`.

`fly.pricing.toml` clones the payments service template:
- `min_machines_running = 1`
- Single-machine deploy (`--ha=false`)
- env: `ConnectionStrings__pricing`, `RabbitMq__Host`, `Pricing__TaxProvider = "RateTable"`

**bootstrap.sh changes (P1 brief):**
```bash
INTERNAL_APPS=(
  haworks-identity haworks-catalog haworks-orders
  haworks-payments haworks-checkout
  haworks-pricing                         # new
  ...
)
```

**deploy.yml changes:** add `"pricing"` to the matrix builder in the `plan` job.

Cost: ~$2–3/mo (one shared-cpu-1x machine + existing Neon free tier covers the DB).

---

## 11. Test plan

Three layers.

### 11.1 Unit (`tests/Pricing.Unit/`)

- `PriceRuleTests` — factory invariants, tiered price non-overlap validation.
- `CalculationEngineTests` — `CalculateEffectivePriceQueryHandler` with mock repositories:
  - Base price with no rules returns base × qty.
  - Volume tier applies correct tier UnitPrice.
  - Percentage discount stacks correctly.
  - FixedAmount discount floors at zero.
  - Promotion code percentage applied to subtotal.
  - Promotion code fixed applied to subtotal (floor at zero).
  - Expired promotion returns validation failure.
  - Two conflicting rules: higher priority wins.
- `TaxRateTests` — `RateTableTaxCalculator` with seeded in-memory rate table:
  - US/CA returns correct combined rate.
  - US/TX returns correct combined rate.
  - Unknown jurisdiction throws `TaxCalculationException`.
  - Null jurisdiction returns `TaxAmount = 0`.
- `PromotionRedemptionTests` — `TryRedeemAsync` with mock: exhausted code returns false, per-user limit enforced.
- `SnapshotTests` — `PriceCalculationLog` captures all fields correctly.
- Coverage target: > 90% on Domain + Application (excluding generated EF code).

### 11.2 Integration (`tests/Pricing.Integration/`)

Use `SharedTestPostgres.CreateDatabaseAsync("pricing")` from `BuildingBlocks.Testing.Containers`. **Do NOT create raw `PostgreSqlBuilder` containers.**

- `PricingWebAppFactory` — mirrors `PaymentsWebAppFactory`; Testcontainers Postgres via shared singleton, env-vars-before-build, `EnsureSchemaAsync()`, MassTransit test harness.
- `CalculateEffectivePrice_returns_correct_breakdown_for_tiered_rules`
- `CalculateEffectivePrice_applies_valid_promotion_code`
- `CalculateEffectivePrice_rejects_expired_promotion_code` (422)
- `CalculateEffectivePrice_includes_tax_for_known_jurisdiction`
- `CalculateEffectivePrice_returns_500_for_unknown_jurisdiction` (fail-closed)
- `RedeemPromotion_is_idempotent_by_orderId`
- `RedeemPromotion_returns_409_when_maxuses_exhausted`
- `AdminCreatePriceRule_returns_201_and_rule_is_applied_to_subsequent_calculations`
- `AdminDeletePromotion_softdeletes_in_flight_redemptions_are_honoured`
- Catalog HTTP client: mock with WireMock; verify 60s cache prevents duplicate calls.
- `ProductCacheInvalidatedEvent_clears_cached_base_price` (MassTransit test harness).

### 11.3 Smoke (`tests/Smoke/`)

One assertion appended: `GET <bff>/pricing/calculate?productId=<known>&quantity=1` returns 200 with `total > 0` in < 1s post-deploy.

---

## 12. Observability

- **Traces:** OpenTelemetry via `BuildingBlocks.Telemetry`. Spans: `Pricing.Calculate`, `Pricing.TaxLookup`, `Pricing.PromotionValidate`, `Pricing.PromotionRedeem`. Tag `pricing.rule_count` (rules applied), `pricing.promotion_applied` (bool).
- **Metrics:** `pricing_calculations_total{result=success|error}`, histogram `pricing_calculation_duration_ms`, counter `pricing_promotions_redeemed_total`, counter `pricing_promotions_rejected_total{reason=expired|exhausted|invalid}`.
- **Logs:** structured; log rule IDs and promo code applied (not dollar amounts in log bodies — amounts are in the DB audit log).
- **Alerts:** `pricing_calculations_total{result=error}` rate > 1% over 5 min → page.

---

## 13. Failure modes & runbook stubs

| Failure | Detection | Mitigation |
|---|---|---|
| Catalog HTTP client down → BaseUnitPrice unavailable | 5xx burst on `Pricing.Calculate` span | Polly retry (3× with exponential backoff); after max retries → 503; CheckoutSaga receives `PricingFailedEvent`, compensates |
| Tax rate missing for new jurisdiction | HTTP 500 from `/calculate` | Admin adds rate via `POST /admin/pricing/tax/rates`; retry checkout |
| Promotion race: two users exhaust last redemption simultaneously | Second request returns 409 | Client shows "promo code no longer available" — correct behaviour |
| PriceRule admin deletes rule mid-checkout | Rule is soft-deleted; in-flight `calculationId` snapshot is unaffected | Saga holds snapshot; no action needed |
| Calculation log table grows unbounded | Alert on table size > 10 GB | Partition by `CalculatedAt` (monthly); archive or drop partitions > 2 years |
| pricing-svc unreachable during checkout | CheckoutSaga HTTP call times out (5s) | `PricingFailedEvent` → StockReleaseRequested → Abandoned; customer sees retry message |

---

## 14. Implementation plan (Gemini CLI agents)

Seven self-contained briefs in `docs/agent-briefs/pricing/`. Each brief is one Gemini CLI invocation. **Hard checkpoints between phases — the user reviews the done-report and only then launches the next phase.**

```
Phase 1: Scaffold (1 agent, sequential — blocks everything)
  P1  Fill src/Pricing/ skeleton:
        Pricing.Domain csproj (refs: BuildingBlocks.Domain)
        Pricing.Application csproj (refs: Domain, BuildingBlocks.Application)
        Pricing.Infrastructure csproj (refs: Application, BuildingBlocks.Infrastructure, EF Core)
        Pricing.Api csproj (refs: Infrastructure, Pricing.Application)
        Program.cs with AddPricingDbContext, AddMassTransit, UseOpenTelemetry
        fly.pricing.toml + bootstrap.sh entry + deploy.yml matrix entry
        Empty test project shells: tests/Pricing.Unit/, tests/Pricing.Integration/
        New contract records in src/Contracts/Pricing/:
          PriceCalculatedEvent, PromotionRedeemedEvent, PricingFailedEvent
      → CHECKPOINT: dotnet build clean, dotnet test (empty projects) green,
        flyctl config validate fly.pricing.toml passes.

Phase 2: Three independent tracks — fire all three in parallel
  P2  Domain model + calculation engine (no persistence yet):
        PriceRule, TieredPrice, PromotionCode, PromotionRedemption,
        TaxRate, PriceCalculationLog in Pricing.Domain/
        CalculateEffectivePriceQueryHandler in Pricing.Application/
          (catalog HTTP client stubbed via ICatalogPricingClient interface)
        All domain invariant unit tests in tests/Pricing.Unit/
      → CHECKPOINT: all unit tests in §11.1 green.

  P3  Promotion code commands:
        CreatePromotionCodeCommand + handler
        ValidatePromotionCodeQuery + handler
        ApplyPromotionCodeCommand + handler (includes atomic CAS UPDATE via ExecuteSqlRawAsync)
        PromotionRedemption entity + repository
        Promotion-specific unit tests from §11.1
      → CHECKPOINT: promotion unit tests green; CAS logic covered by mock tests.

  P4  Tax calculation module:
        ITaxCalculator interface
        RateTableTaxCalculator (Infrastructure)
        AvalaraTaxCalculator stub (Infrastructure, no-op, registered via feature flag)
        TaxRate entity + TaxRateRepository
        Tax unit tests from §11.1
        Seed data: all 50 US state combined rates for 2026, UK (0.20), CA (0.05), AU (0.10)
      → CHECKPOINT: tax unit tests green; seed data script generates valid SQL.

Phase 3: Persistence + API — two parallel agents (both depend on P2+P3+P4)
  P5  Persistence and migrations:
        PricingDbContext with all entities
        EF Core migrations (initial + seed data for TaxRates)
        Repository implementations for PriceRule, PromotionCode, TaxRate, CalculationLog
        PricingWebAppFactory integration test factory
        All integration tests from §11.2
      → CHECKPOINT: dotnet ef migrations script runs; all integration tests green.

  P6  REST API layer + admin endpoints:
        PricingController (GET /pricing/calculate, POST /promotions/validate, POST /promotions/redeem)
        TaxController (GET /pricing/tax/rate)
        AdminController (full CRUD for rules, promotions, tax rates) gated by [Authorize(Roles="admin")]
        BFF proxy route (if BFF is in scope for this brief — see §3.1 note)
        Smoke test entry from §11.3
      → CHECKPOINT: dotnet test integration tests covering all controller endpoints green.

Phase 4: Checkout integration (1 agent, sequential — depends on P5+P6)
  P7  Wire pricing into CheckoutSaga:
        New saga state fields: PricingCalculationId, PricedSubtotal, PricedTaxAmount, PricedTotal
        New saga states: PricingRequested, PricingCompleted
        New saga events (in src/Contracts/Pricing/): PricingRequestedEvent, PricingCompletedEvent, PricingFailedEvent
        CheckoutSaga flow: StockReserved → publish PricingRequestedEvent → await PricingCompletedEvent
          → publish PaymentSessionRequestedEvent (using PricedTotal, including Tax on the event)
        PricingConsumer in Pricing.Api that handles PricingRequestedEvent:
          calls CalculateEffectivePriceQuery, publishes PricingCompletedEvent or PricingFailedEvent
        Updated PaymentSessionRequestedEvent: pass Tax field (already exists on the contract as `decimal Tax`)
        Saga compensation: PricingFailedEvent → release stock → Abandoned
        CheckoutOrchestrator.Infrastructure migration (new state fields on saga table)
        Integration test: full saga happy path with mocked pricing-svc returning known breakdown
      → CHECKPOINT: end-to-end saga test with mocked pricing-svc passes; checkout total sent
        to payment provider equals PricedTotal (not catalog UnitPrice × qty).
```

**Anti-stuck rules baked into every brief.** Repeated for emphasis since Gemini CLI agents lose context faster than humans:

1. Read the **Inputs** section before writing any code. Don't grep the codebase blindly.
2. Stay inside the **Deliverable** scope. If you see a tempting refactor, **don't do it** — note it in the done-report under "out-of-scope observations" instead.
3. **Acceptance** commands are non-negotiable. If they don't pass, you're not done. If they can't pass for a reason outside your control, write a `blocker:` line per the protocol doc and stop.
4. Hard time budget per brief: ~30 min of agent time. If stuck past 30 min, stop and emit a blocker — don't keep retrying.
5. **No cross-brief edits.** P3 must not modify the calculation engine handler; that's P2's territory. If you discover P2 missed something, file a blocker, don't patch.
6. Done-report format is fixed (see `docs/agent-briefs/audit-protocol.md`). Stick to it.
7. **Never create raw Testcontainers** (`PostgreSqlBuilder`, `ContainerBuilder`). Always use `SharedTestPostgres.CreateDatabaseAsync("pricing")` from `BuildingBlocks.Testing.Containers`. CI will reject raw container usage.

---

## 15. Key decisions log (2026-05-16)

| Question | Decision |
|---|---|
| Tax calculation: fail-open or fail-closed? | **Fail-closed** (HTTP 500 → saga compensates). Revenue leak from silent zero-tax is worse than a failed checkout. |
| Multi-currency v1? | **No** — USD only. Domain model uses `currency` string field throughout so v2 multi-currency is additive. |
| Price snapshot in saga | **Yes** — captured at `PricingCompleted` transition, immutable for saga lifetime. Catalog price changes mid-checkout are ignored. |
| Expired promotion on in-flight checkout | **Honour if `snapshotAt < ExpiresAt`**; reject if applied after expiry. Checked at redemption time using `calculationId` log record. |
| Stacked discounts flooring | **Floor at 0** (no minimum price per product in v1). Negative price is impossible. |
| External tax API | **Avalara deferred to v2**. Stub registered via `Pricing:TaxProvider` config; zero-callsite-change swap. |
| Promotion race condition mitigation | **Atomic CAS UPDATE via `ExecuteSqlRawAsync`**. EF optimistic concurrency alone is insufficient here. |
| Timezone-sensitive promotions | **Store UTC** (ISO 8601 with offset). `SellerTimezone` is display-only metadata. Engine compares against `DateTimeOffset.UtcNow`. |
| Audit log retention | **2 years**, append-only. Partition by `CalculatedAt` monthly once volume justifies it. |
| Admin API auth | **`[Authorize(Roles="admin")]`** — platform admin role claim, same pattern as other admin endpoints. |
| Catalog price caching in pricing-svc | **60s in-memory cache** (IMemoryCache). Invalidated by `ProductCacheInvalidatedEvent`. |
