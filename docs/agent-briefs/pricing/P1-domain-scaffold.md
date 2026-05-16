# P1 — Pricing Domain Scaffold

**Brief:** P1 | **Spec:** `docs/agent-briefs/pricing-tax-engine-spec.md`
**Phase:** 1 (sequential — blocks P2–P7)
**Time budget:** 30 min

---

## Inputs

Read before touching anything:
- `src/Pricing/Pricing.Domain/` — empty shell (only bin/obj exist)
- `src/Pricing/Pricing.Application/` — empty shell
- `src/Pricing/Pricing.Infrastructure/` — empty shell
- `src/Pricing/Pricing.Api/` — empty shell
- `src/Catalog/Catalog.Domain/Product.cs` — confirms `UnitPrice` is `decimal`
- `Directory.Build.props` — shared build props, package versions

---

## Deliverable

Populate `src/Pricing/` from the empty shell. All four projects need `.csproj` files and initial source files.

### Pricing.Domain

Entities (per §4 of the spec):
- `PriceRule.cs` — with `PriceRuleType` enum (`Percentage`, `Absolute`, `BuyXGetY`) and `PriceRuleScope` enum (`Product`, `Category`, `Cart`)
- `PromotionCode.cs` — with `CanRedeem(DateTimeOffset at)` method
- `TieredPrice.cs`

Value objects:
- `PriceQuote.cs` (sealed record)
- `PriceQuoteLine.cs` (sealed record)

All entities extend `AuditableEntity` from `BuildingBlocks.Domain` (look at how Catalog.Domain does it).

### Pricing.Application

Interfaces:
- `ITaxCalculationAdapter.cs` — with `TaxRequest`, `TaxResult`, `TaxLineItem` records
- `ICatalogPricingClient.cs` — Refit interface stub (one method: `GetProductAsync(Guid id)`)

Placeholder service classes (empty implementation, marked with `// TODO: R2/P2`):
- `PriceCalculationEngine.cs`
- `TaxCalculationService.cs`

Consumer placeholder:
- `PricingRequestedConsumer.cs` (empty, registers with MassTransit)

### Pricing.Infrastructure

- `PricingDbContext.cs` — EF Core DbContext with `DbSet<PriceRule>`, `DbSet<PromotionCode>`, `DbSet<TieredPrice>`, `DbSet<PriceQuoteCache>`
- `PriceQuoteCache.cs` entity — `IdempotencyKey`, `UserId`, `PriceQuoteJson`, `ExpiresAt`
- Initial EF migration: `dotnet ef migrations add InitialCreate -p Pricing.Infrastructure -s Pricing.Api`

### Pricing.Api

- `Program.cs` — standard Haworks service bootstrap (copy from Catalog.Api/Program.cs pattern, change service name)
- `appsettings.json` — include `Tax:FailOpen: false`, `Tax:Rates` array (see §6.2)
- `fly.pricing.toml` — copy from `fly.catalog.toml`, change `app = "haworks-pricing"`, same `shared-cpu-1x 256MB iad` config

### bootstrap.sh

Add `haworks-pricing` to `INTERNAL_APPS` array (wherever catalog/orders are listed).

### deploy.yml

Add `"pricing"` to the matrix builder in the `plan` job (same pattern as `"search"` and `"catalog"`).

---

## Acceptance

```bash
dotnet build src/Pricing/
dotnet ef migrations list --project src/Pricing/Pricing.Infrastructure --startup-project src/Pricing/Pricing.Api
# → shows "InitialCreate"
flyctl config validate fly.pricing.toml
```

---

## Anti-stuck

- `UnitPrice` is `decimal` everywhere — never use `double` or `float` for any price field.
- `AuditableEntity` base class: find it via `grep -r "class AuditableEntity" src/BuildingBlocks/` and copy the same inheritance pattern used by Catalog entities.
- Do NOT add any Refit, Polly, or VaultSharp packages to Pricing.Domain — domain has zero infrastructure dependencies.
- `PriceQuoteCache` is an EF entity in the Infrastructure project, NOT in Domain. It is an implementation detail, not a domain concept.
- If `Directory.Build.props` already pins EF Core version, do not add an explicit `PackageReference` for EF Core in the csproj — it will conflict.

---

## Done-report format

```
brief: P1
status: done | blocked
files_changed:
  - src/Pricing/Pricing.Domain/Pricing.Domain.csproj
  - src/Pricing/Pricing.Domain/Entities/PriceRule.cs
  - src/Pricing/Pricing.Domain/Entities/PromotionCode.cs
  - src/Pricing/Pricing.Domain/Entities/TieredPrice.cs
  - src/Pricing/Pricing.Domain/ValueObjects/PriceQuote.cs
  - src/Pricing/Pricing.Domain/ValueObjects/PriceQuoteLine.cs
  - src/Pricing/Pricing.Application/Pricing.Application.csproj
  - src/Pricing/Pricing.Application/Interfaces/ITaxCalculationAdapter.cs
  - src/Pricing/Pricing.Application/Interfaces/ICatalogPricingClient.cs
  - src/Pricing/Pricing.Application/Services/PriceCalculationEngine.cs
  - src/Pricing/Pricing.Application/Services/TaxCalculationService.cs
  - src/Pricing/Pricing.Infrastructure/Pricing.Infrastructure.csproj
  - src/Pricing/Pricing.Infrastructure/Persistence/PricingDbContext.cs
  - src/Pricing/Pricing.Infrastructure/Persistence/Entities/PriceQuoteCache.cs
  - src/Pricing/Pricing.Infrastructure/Migrations/YYYYMMDD_InitialCreate.cs
  - src/Pricing/Pricing.Api/Pricing.Api.csproj
  - src/Pricing/Pricing.Api/Program.cs
  - src/Pricing/Pricing.Api/appsettings.json
  - fly.pricing.toml
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
