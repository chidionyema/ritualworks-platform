# P3 — ConfigurableRateTaxAdapter

**Brief:** P3 | **Spec:** `docs/agent-briefs/pricing-tax-engine-spec.md`
**Phase:** 2 (parallel with P2, P4 — all require P1 complete)
**Time budget:** 20 min

---

## Inputs

- `src/Pricing/Pricing.Application/Interfaces/ITaxCalculationAdapter.cs` (created by P1)
- `src/Pricing/Pricing.Api/appsettings.json` (created by P1 — already has Tax:Rates section)

---

## Deliverable

### ConfigurableRateTaxAdapter

`src/Pricing/Pricing.Infrastructure/Adapters/ConfigurableRateTaxAdapter.cs`

Rate resolution (strictly in this order):
1. Match `Country + State` exact (case-insensitive).
2. Fall back to `Country + State=null`.
3. Fall back to `Country="*" + State=null` (wildcard).
4. If no match:
   - `FailOpen=false` (default): return 0% rate, log `Warning` with `SecretPath`-style structured key `{SecretPath: "tax/rate/missing", Country: X, State: Y}`.
   - `FailOpen=true`: throw `TaxCalculationException("No tax rate configured for {Country}/{State}")`.

Tax calculation:
- For each line item: `LineTax = Round(line.LineTotal * rate, 2, MidpointRounding.AwayFromZero)`.
- `TotalTax = sum(LineTaxes)`.
- Return `TaxResult { TotalTax, LineTaxes, Method = "ConfigurableRate" }`.

Registration in `Pricing.Infrastructure/DependencyInjection.cs`:
```csharp
services.Configure<TaxOptions>(configuration.GetSection("Tax"));
services.AddSingleton<ITaxCalculationAdapter, ConfigurableRateTaxAdapter>();
```

`TaxOptions` record (in Application layer):
```csharp
public sealed class TaxOptions
{
    public bool FailOpen { get; set; } = false;
    public List<TaxRateEntry> Rates { get; set; } = new();
}
public sealed class TaxRateEntry
{
    public string Country { get; set; } = "*";
    public string? State { get; set; }
    public decimal Rate { get; set; }
}
```

---

## Acceptance

```bash
dotnet test tests/Pricing.Unit/ --filter "Category=TaxAdapter"
```

Unit tests required (tag with `[Trait("Category", "TaxAdapter")]`):
- `ConfigurableRateTaxAdapter_matches_exact_country_and_state`
- `ConfigurableRateTaxAdapter_falls_back_to_country_wildcard`
- `ConfigurableRateTaxAdapter_falls_back_to_global_wildcard`
- `ConfigurableRateTaxAdapter_returns_zero_when_no_match_and_fail_open_false`
- `ConfigurableRateTaxAdapter_throws_when_no_match_and_fail_open_true`
- `ConfigurableRateTaxAdapter_rounds_tax_correctly`

---

## Anti-stuck

- `TaxOptions` is in `Pricing.Application` (not Infrastructure) — it is a plain options class with no infrastructure dependencies.
- `ConfigurableRateTaxAdapter` is in `Pricing.Infrastructure` because it reads from `IOptions<TaxOptions>` which is an infrastructure concern.
- Country/state matching is case-insensitive. `"US"` and `"us"` must match the same rate.
- `TaxCalculationException` is a domain exception — define it in `Pricing.Domain/Exceptions/`.

---

## Done-report format

```
brief: P3
status: done | blocked
files_changed:
  - src/Pricing/Pricing.Application/Options/TaxOptions.cs
  - src/Pricing/Pricing.Domain/Exceptions/TaxCalculationException.cs
  - src/Pricing/Pricing.Infrastructure/Adapters/ConfigurableRateTaxAdapter.cs
  - src/Pricing/Pricing.Infrastructure/DependencyInjection.cs
  - tests/Pricing.Unit/ConfigurableRateTaxAdapterTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
