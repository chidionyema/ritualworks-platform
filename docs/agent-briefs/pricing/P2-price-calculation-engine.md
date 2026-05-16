# P2 — Price Calculation Engine

**Brief:** P2 | **Spec:** `docs/agent-briefs/pricing-tax-engine-spec.md`
**Phase:** 2 (parallel with P3, P4 — all require P1 complete)
**Time budget:** 30 min

---

## Inputs

- `src/Pricing/Pricing.Domain/Entities/PriceRule.cs` (created by P1)
- `src/Pricing/Pricing.Domain/Entities/TieredPrice.cs` (created by P1)
- `src/Pricing/Pricing.Domain/ValueObjects/PriceQuote.cs` (created by P1)
- `src/Pricing/Pricing.Application/Services/PriceCalculationEngine.cs` (placeholder from P1)

---

## Deliverable

Implement `PriceCalculationEngine` (§5.1 of the spec). Inputs: `IReadOnlyList<CartItem>`, `IReadOnlyList<PriceRule>`, `IReadOnlyList<TieredPrice>`, optional `PromotionCode`. Output: `PriceQuote`.

`CartItem` record (define in `Pricing.Domain/ValueObjects/`):
```csharp
public sealed record CartItem
{
    public required Guid ProductId { get; init; }
    public required string ProductName { get; init; }
    public required int Quantity { get; init; }
    public required decimal CatalogUnitPrice { get; init; }  // fetched from catalog
}
```

Rule evaluation pipeline (strictly follow §5.1 ordering):
1. Apply `TieredPrice` override if `Quantity >= TieredPrice.MinQuantity` (pick highest-threshold tier that applies).
2. Collect `PriceRule` items where `IsApplicableTo(item, utcNow)` is true, ordered by `Priority ASC`.
3. Apply `Percentage` rules first (multiply by `1 - Value/100`), then `Absolute` rules (subtract `Value`).
4. If any rule has `IsStackable=false`, take only the single best-priority matching rule for that item.
5. Clamp `FinalUnitPrice = Math.Max(0, computed)`.
6. `LineTotal = FinalUnitPrice * Quantity`.

Cart-level rules (`Scope=Cart`):
7. Collect cart-scope rules, apply to `Subtotal` (sum of LineTotals) in the same Percentage→Absolute order.
8. Clamp `PostDiscountSubtotal = Math.Max(0, Subtotal - CartDiscount)`.

Promotion code (called after cart rules):
9. `PromotionCode` carries a `PriceRule` reference — apply that rule to `PostDiscountSubtotal`.
10. The `PromotionCode.CanRedeem(utcNow)` check and `Redemptions` increment are NOT done here — that is the consumer's responsibility. Engine only applies the math.

Output:
```csharp
return new PriceQuote
{
    QuoteId     = Guid.NewGuid(),
    QuotedAt    = utcNow,
    ExpiresAt   = utcNow.AddMinutes(15),
    Lines       = lines,
    Subtotal    = subtotal,
    DiscountTotal = discountTotal,
    TaxTotal    = 0m,   // filled in by TaxCalculationService after engine returns
    FinalTotal  = subtotal - discountTotal,  // tax added by caller
    Currency    = "USD",
    AppliedRuleNames = appliedNames,
    PromotionCode = promotionCode?.Code,
};
```

---

## Acceptance

```bash
dotnet test tests/Pricing.Unit/ --filter "Category=PriceEngine"
```

All 11 unit tests from §12.1 of the spec covering the engine must pass. Tag them `[Trait("Category", "PriceEngine")]`.

---

## Anti-stuck

- No database calls, no HTTP calls, no DI in this service — it is a pure function. Input comes from the caller; this engine just does the math.
- `BuyXGetY` rule type is defined in the enum but NOT implemented in v1. If `Type == BuyXGetY`, log a warning and skip the rule. Do not throw.
- Rounding: use `MidpointRounding.AwayFromZero` for all `Math.Round` calls on price values. Keep at least 4 decimal places internally; round to 2 decimals only in the final `PriceQuoteLine.LineTotal` and `PriceQuote.FinalTotal`.
- The engine is stateless and should be registered as `Singleton` (no scoped dependencies).

---

## Done-report format

```
brief: P2
status: done | blocked
files_changed:
  - src/Pricing/Pricing.Domain/ValueObjects/CartItem.cs
  - src/Pricing/Pricing.Application/Services/PriceCalculationEngine.cs
  - tests/Pricing.Unit/PriceCalculationEngineTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
