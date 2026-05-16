# P5 — PricingRequestedConsumer + POST /price-quote Endpoint

**Brief:** P5 | **Spec:** `docs/agent-briefs/pricing-tax-engine-spec.md`
**Phase:** 3 (parallel with P6 — both require P2+P3+P4 complete)
**Time budget:** 30 min

---

## Inputs

- `src/Pricing/Pricing.Application/Services/PriceCalculationEngine.cs` (from P2)
- `src/Pricing/Pricing.Infrastructure/Adapters/ConfigurableRateTaxAdapter.cs` (from P3)
- `src/Pricing/Pricing.Application/Interfaces/ICatalogPricingClient.cs` (from P4)
- `src/Pricing/Pricing.Infrastructure/Persistence/PricingDbContext.cs` (from P1)
- Spec §7 (API contracts) and §5.2 (idempotency)

---

## Deliverable

### New contracts (`src/Contracts/Pricing/`)

Three event records as defined in §7.3 of the spec:
- `PricingRequestedEvent.cs`
- `PriceQuoteConfirmedEvent.cs`
- `PriceQuoteRejectedEvent.cs`

### PricingRequestedConsumer

`src/Pricing/Pricing.Application/Consumers/PricingRequestedConsumer.cs`

On receive:
1. Check `PriceQuoteCache` for `(IdempotencyKey, UserId)` — if found and not expired, publish `PriceQuoteConfirmedEvent` using cached data and return.
2. For each item in `PricingRequestedEvent.Items`: call `ICatalogPricingClient.GetProductAsync`. On 404 or HTTP error after retries: publish `PriceQuoteRejectedEvent { Reason = "product_not_found" }` and return.
3. Load applicable `PriceRule` and `TieredPrice` from DB (filter by applicability — product scope, category scope, or cart scope).
4. If `PromotionCode` is provided: load from DB. Delegate `CanRedeem` check to P6's promo code command (publish a `RedeemPromotionCodeCommand` and await response via MassTransit request/response — OR, simpler for v1: check inline without lock, and let P6's endpoint handle the locking for web requests). For saga-mediated flows, promo code is validated and locked in step 4 of the consumer.
5. Call `PriceCalculationEngine.Calculate(items, rules, tieredPrices, promoCode)`.
6. Call `ITaxCalculationAdapter.CalculateAsync(taxRequest, ct)`. On `TaxCalculationException`: publish `PriceQuoteRejectedEvent { Reason = "tax_calculation_failed" }` and return.
7. Assemble `PriceQuote`, store in `PriceQuoteCache` with `ExpiresAt = quote.ExpiresAt`.
8. Publish `PriceQuoteConfirmedEvent`.

### PriceQuoteController

`src/Pricing/Pricing.Api/Controllers/PriceQuoteController.cs`

```
POST /price-quote
[Authorize]
Header: Idempotency-Key: <uuid>
Body: { items, promotionCode?, destinationCountry, destinationState? }

→ 200 PriceQuoteResponse
→ 400 if items empty / quantity <= 0
→ 409 if promo code exhausted (returned from RedeemPromotionCode step)
→ 422 if FinalTotal <= 0
```

The controller sends a `GetPriceQuoteQuery` via MediatR. The query handler follows the same pipeline as the consumer (reuse `PriceCalculationEngine`). For the web endpoint, promo code redemption goes through P6's `RedeemPromotionCodeCommand` (pessimistic lock). For v1 before P6 is done, stub with a TODO comment.

---

## Acceptance

```bash
dotnet test tests/Pricing.Integration/ --filter "Category=PriceQuote"
```

All integration tests from §12.2 of the spec (excluding promo code race condition, which is P6):
- `PostPriceQuote_returns_correct_total_for_known_products`
- `PostPriceQuote_applies_promo_code_and_decrements_redemption_count`
- `PostPriceQuote_returns_409_when_promo_code_exhausted`
- `PostPriceQuote_is_idempotent_with_same_idempotency_key`
- `PostPriceQuote_returns_correct_tax_for_CA_address`
- `PricingRequestedConsumer_publishes_confirmed_event_on_success`
- `PricingRequestedConsumer_publishes_rejected_event_when_catalog_unavailable`
- `PricingRequestedConsumer_publishes_rejected_event_when_tax_fails_closed`

---

## Anti-stuck

- `PriceQuoteCache` check must use `(IdempotencyKey, UserId)` composite — not just `IdempotencyKey`. Two users with the same key should not share a cache hit.
- `PriceQuoteConfirmedEvent.PriceQuoteJson` is the full serialized `PriceQuote` — serialize with `System.Text.Json.JsonSerializer.Serialize(quote)`.
- The consumer must be registered with MassTransit in `Pricing.Api/Program.cs`. Follow the existing consumer registration pattern from Catalog or Orders.
- If the `GetPriceQuoteQuery` handler and the `PricingRequestedConsumer` share the same pipeline logic, extract it into a `PriceQuotingService` to avoid duplication — do not copy-paste.
- `FinalTotal <= 0` returns 422, not 400 — it indicates a domain constraint violation, not a validation error.

---

## Done-report format

```
brief: P5
status: done | blocked
files_changed:
  - src/Contracts/Pricing/PricingRequestedEvent.cs
  - src/Contracts/Pricing/PriceQuoteConfirmedEvent.cs
  - src/Contracts/Pricing/PriceQuoteRejectedEvent.cs
  - src/Pricing/Pricing.Application/Consumers/PricingRequestedConsumer.cs
  - src/Pricing/Pricing.Application/Queries/GetPriceQuoteQuery.cs
  - src/Pricing/Pricing.Application/Services/PriceQuotingService.cs
  - src/Pricing/Pricing.Api/Controllers/PriceQuoteController.cs
  - src/Pricing/Pricing.Api/Program.cs
  - tests/Pricing.Integration/PriceQuoteTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
