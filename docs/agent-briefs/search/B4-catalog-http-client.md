# B4 — Catalog HTTP client in search-svc

## Goal

Add a typed HTTP client in `Search.Infrastructure` that fetches an enriched product (Product + Category) from catalog over flycast, with Polly retry + timeout policies, plus a `GET /api/products?skip=&take=` listing for backfill. Tested against a WireMock stub.

**Two facts the brief must respect — verified against the catalog code:**
1. The list endpoint uses **offset pagination** (`?skip={int}&take={int}&categoryId={guid?}`), not cursor pagination.
2. The list-projection sets `categoryName = null` for performance reasons. **Only `GET /api/products/{id}` returns the denormalized `categoryName`.** The backfill flow is therefore: list to get IDs → per-ID enrichment → upsert. Indexer events (B5) only ever use the per-ID endpoint.

The codebase **does not currently use Refit** — every existing service-to-service call uses raw `IHttpClientFactory` via `AddHttpClient<TInterface, TImpl>`. This brief sticks with that pattern. Don't introduce Refit.

## Phase / blocks-on

Phase 2. Blocks-on: B1 done. **Independent of B2 and B3** — runs in parallel.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §3.2 (events) and §5 (indexer pipeline) — confirms the read-API contract you're consuming.
3. `src/Catalog/Catalog.Api/Controllers/ProductsController.cs` — read the existing `GET /api/products/{id:guid}` endpoint and its response DTO. **The DTO you build here must match what catalog actually returns.** If the projection misses Category data needed by the spec, file a blocker — do not modify catalog (cross-brief scope).
4. `src/BuildingBlocks/Resilience/` — there's already a `ResiliencePolicyFactory`. Use it; don't write a new Polly setup from scratch.
5. `src/BffWeb/BffWeb.Api/Program.cs` — find any `services.AddHttpClient<...>(...)` block. That's the established service-to-service HTTP-client pattern in this repo. Mirror it.
6. `src/Search/Search.Infrastructure/Search.Infrastructure.csproj` — confirm `WireMock.Net` isn't yet listed for tests (if it is, just use it; if not, you'll add it test-side).

## Deliverable

### NuGet additions

`WireMock.Net` (latest stable) added to `Directory.Build.props` (centrally pinned) **test-only** — only reference it from `tests/Search.Integration/Search.Integration.csproj`. No production-side packages added by this brief.

### Typed-client interface

`src/Search/Search.Infrastructure/Catalog/ICatalogProductsApi.cs`:

```csharp
public interface ICatalogProductsApi
{
    Task<CatalogProductDto> GetProductAsync(Guid id, CancellationToken ct);

    // Offset pagination, matching catalog's actual API. CategoryName on the
    // returned items is null — backfill must enrich each via GetProductAsync.
    Task<CatalogProductPage> ListProductsAsync(int skip, int take, Guid? categoryId, CancellationToken ct);
}
```

`src/Search/Search.Infrastructure/Catalog/CatalogProductsApiClient.cs` — concrete implementation that takes an `HttpClient` (injected by `IHttpClientFactory`) and uses `System.Net.Http.Json`'s `GetFromJsonAsync<T>` to call `/api/products/{id}` and `/api/products?skip={n}&take={n}&categoryId={guid?}`. Throws on non-2xx; let the policy handler retry and surface the failure.

`CatalogProductDto` and `CatalogProductPage` POCOs in `src/Search/Search.Infrastructure/Catalog/`. **Field names match catalog's actual API response** — verified shapes:

```csharp
public sealed record CatalogProductDto(
    Guid Id, string Name, string Description, decimal UnitPrice,
    int StockQuantity, bool IsInStock, bool IsListed,
    Guid CategoryId, string? CategoryName);          // null on list, populated on get-by-id

public sealed record CatalogProductPage(
    IReadOnlyList<CatalogProductDto> Items, int Total, int Skip, int Take);
```

### DI registration

In `Search.Infrastructure.DependencyInjection.AddInfrastructure(...)`:

```csharp
services.AddHttpClient<ICatalogProductsApi, CatalogProductsApiClient>(c =>
    {
        c.BaseAddress = new Uri(configuration["Catalog:BaseAddress"]
            ?? "http://haworks-catalog.flycast:8080");
        c.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddPolicyHandler((sp, _) =>
    {
        var factory = sp.GetRequiredService<IResiliencePolicyFactory>();
        return factory.CreateCombinedPolicy(ResilienceOptions.ForExternalApi("catalog"));
    });
```

`ResilienceOptions.ForExternalApi(string serviceName, bool includeBulkhead = true)` is the canonical preset for cross-service HTTP calls (see `src/BuildingBlocks/Resilience/IResiliencePolicyFactory.cs`). Use it as-is — do not invent a new `ResilienceOptions.Catalog` preset, do not stuff a new Polly chain inline.

### Tests

`tests/Search.Integration/CatalogProductsApiTests.cs`:

1. `GetProductAsync_returns_dto_when_catalog_returns_200` — WireMock stub returns a canned JSON, assert the client deserializes it.
2. `GetProductAsync_throws_after_retry_when_catalog_5xx` — WireMock returns 500; assert we retry per the resilience policy and ultimately throw.
3. `GetProductAsync_throws_404_for_unknown_product` — assert an `HttpRequestException` (or whatever the policy surfaces) with status 404.
4. `ListProductsAsync_paginates_via_skip_take` — WireMock returns two pages (skip=0/take=100 and skip=100/take=100); assert the client requests both with the correct query string and surfaces `Total`/`Skip`/`Take` correctly.

These tests do **not** start a SearchWebAppFactory — they spin up just the DI container with the WireMock URL.

## Acceptance

```bash
dotnet build HaworksPlatform.sln -c Release
dotnet test tests/Search.Integration -c Release --filter "FullyQualifiedName~CatalogProductsApiTests"
dotnet test tests/Search.Integration -c Release    # full suite still green
```

All green.

## Hard stops

- Do **not** call out to a real catalog instance from tests. WireMock only.
- Do **not** modify catalog code, even to "fix" a missing field on the response DTO. File a blocker if the response shape doesn't match the spec.
- Do **not** add a consumer or an endpoint here.
- Do **not** instantiate Polly policies inline; use `IResiliencePolicyFactory`.
- Do **not** introduce Refit. The repo does not currently use it; stay with raw `AddHttpClient<TInterface, TImpl>`.

## Done-report

Standard format. Confirm:
- 4 new tests pass.
- The typed client uses `IResiliencePolicyFactory`, not inline Polly.
- The DTO field names exactly match what catalog's controller returns (call out the controller path you verified against).
