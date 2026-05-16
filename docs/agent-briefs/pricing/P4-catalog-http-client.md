# P4 — Catalog HTTP Client (Refit)

**Brief:** P4 | **Spec:** `docs/agent-briefs/pricing-tax-engine-spec.md`
**Phase:** 2 (parallel with P2, P3 — all require P1 complete)
**Time budget:** 20 min

---

## Inputs

- `src/Pricing/Pricing.Application/Interfaces/ICatalogPricingClient.cs` (created by P1)
- `src/Search/Search.Infrastructure/DependencyInjection.cs` — for the Refit + Polly pattern (if search-svc uses it)

---

## Deliverable

### CatalogProductDto

`src/Pricing/Pricing.Application/Models/CatalogProductDto.cs`:
```csharp
public sealed record CatalogProductDto
{
    public Guid ProductId { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal UnitPrice { get; init; }
    public bool IsInStock { get; init; }
    public bool IsListed { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
}
```

### ICatalogPricingClient (Refit interface)

```csharp
public interface ICatalogPricingClient
{
    [Get("/api/products/{id}")]
    Task<ApiResponse<CatalogProductDto>> GetProductAsync(Guid id, CancellationToken ct = default);
}
```

### CatalogPricingClient registration

In `Pricing.Infrastructure/DependencyInjection.cs`:
```csharp
services.AddRefitClient<ICatalogPricingClient>()
    .ConfigureHttpClient(c =>
    {
        c.BaseAddress = new Uri(configuration["Catalog:BaseUrl"]
            ?? "http://haworks-catalog.flycast:8080");
        c.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddPolicyHandler(GetRetryPolicy());

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy() =>
    HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(3, retry => TimeSpan.FromMilliseconds(200 * Math.Pow(2, retry)));
```

Add `Catalog:BaseUrl` to `Pricing.Api/appsettings.json`:
```json
{
  "Catalog": {
    "BaseUrl": "http://haworks-catalog.flycast:8080"
  }
}
```

---

## Acceptance

```bash
dotnet build src/Pricing/
dotnet test tests/Pricing.Integration/ --filter "Category=CatalogClient"
```

Integration test (tag `[Trait("Category", "CatalogClient")]`) using WireMock.Net:
- `CatalogPricingClient_returns_product_on_200`
- `CatalogPricingClient_returns_null_on_404`
- `CatalogPricingClient_retries_on_503_and_succeeds_on_third_attempt`
- `CatalogPricingClient_gives_up_after_3_retries_and_throws`

---

## Anti-stuck

- Use `Refit` and `Polly`, matching the packages already in the solution (check `Directory.Build.props` for versions).
- `ICatalogPricingClient` lives in `Pricing.Application`, NOT Infrastructure — it is an interface. The Refit registration is in Infrastructure.
- The WireMock integration test must use `SharedTestPostgres.CreateDatabaseAsync("pricing")` for the test database if the test host needs a DB context. If the test only tests the HTTP client with a WireMock stub, no DB is needed — do not create a container unnecessarily.
- `ApiResponse<T>` from Refit allows checking `IsSuccessStatusCode` before `.Content` — use this pattern to handle 404 gracefully.

---

## Done-report format

```
brief: P4
status: done | blocked
files_changed:
  - src/Pricing/Pricing.Application/Models/CatalogProductDto.cs
  - src/Pricing/Pricing.Application/Interfaces/ICatalogPricingClient.cs
  - src/Pricing/Pricing.Infrastructure/Http/CatalogPricingClientRegistration.cs
  - src/Pricing/Pricing.Infrastructure/DependencyInjection.cs
  - src/Pricing/Pricing.Api/appsettings.json
  - tests/Pricing.Integration/CatalogPricingClientTests.cs
acceptance_passed: yes | no
blockers: <none or description>
out_of_scope_observations: <optional>
```
