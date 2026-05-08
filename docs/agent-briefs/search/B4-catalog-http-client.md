# B4 — Catalog HTTP client in search-svc

## Goal

Add a typed Refit client in `Search.Infrastructure` that fetches an enriched product (Product + Category) from catalog over flycast, with Polly retry + timeout policies, plus a paginated `GET /api/products` for backfill. Tested against a WireMock stub.

## Phase / blocks-on

Phase 2. Blocks-on: B1 done. **Independent of B2 and B3** — runs in parallel.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §3.2 (events) and §5 (indexer pipeline) — confirms the read-API contract you're consuming.
3. `src/Catalog/Catalog.Api/Controllers/` — find the existing `GET /api/products/{id}` endpoint and read its response DTO. **The DTO you build here must match what catalog actually returns.** If the endpoint doesn't exist or the projection misses Category data, file a blocker — do not modify catalog (that would be cross-brief scope).
4. `src/BuildingBlocks/Resilience/` — there's already a `ResiliencePolicyFactory`. Use it; don't write a new Polly setup from scratch.
5. `src/Search/Search.Infrastructure/Search.Infrastructure.csproj` — confirm Refit and WireMock.NET aren't yet listed (if they are, just use them; if not, add them via `Directory.Build.props`).
6. Any existing service-to-service HTTP client in the repo — search for `[Refit.Get]` or `Refit.RestService` for the established pattern.

## Deliverable

### NuGet additions

Add to `Directory.Build.props` (centrally pinned) if not already present:
- `Refit` (latest stable)
- `Refit.HttpClientFactory` (latest stable)
- `WireMock.Net` (latest stable, **test-only** — only reference from `Search.Integration.csproj`)

### Refit interface

`src/Search/Search.Infrastructure/Catalog/ICatalogProductsApi.cs`:

```csharp
public interface ICatalogProductsApi
{
    [Get("/api/products/{id}")]
    Task<CatalogProductDto> GetProductAsync(Guid id, CancellationToken ct);

    [Get("/api/products")]
    Task<CatalogProductPage> ListProductsAsync(
        [Query] string? cursor,
        [Query] int pageSize,
        CancellationToken ct);
}
```

`CatalogProductDto` and `CatalogProductPage` POCOs in `src/Search/Search.Infrastructure/Catalog/`. **Field names match catalog's actual API response** (read the controller in step 3 above to confirm; do not invent).

### DI registration

In `Search.Infrastructure.DependencyInjection.AddInfrastructure(...)`:

```csharp
services.AddRefitClient<ICatalogProductsApi>()
    .ConfigureHttpClient(c =>
    {
        c.BaseAddress = new Uri(configuration["Catalog:BaseAddress"]
            ?? "http://ritualworks-catalog.flycast:8080");
        c.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddPolicyHandler((sp, _) =>
        sp.GetRequiredService<IResiliencePolicyFactory>()
          .CreateHttpRetryPolicy(ResilienceOptions.CatalogReadApi));
```

If `IResiliencePolicyFactory` doesn't have `CreateHttpRetryPolicy` or `ResilienceOptions.CatalogReadApi` — file a blocker. Do not stuff a new Polly chain inline.

### Tests

`tests/Search.Integration/CatalogProductsApiTests.cs`:

1. `GetProductAsync_returns_dto_when_catalog_returns_200` — WireMock stub returns a canned JSON, assert the Refit client deserializes it.
2. `GetProductAsync_throws_after_retry_when_catalog_5xx` — WireMock returns 500; assert we retry per the resilience policy and ultimately throw.
3. `GetProductAsync_throws_404_for_unknown_product` — assert a `Refit.ApiException` with status 404.
4. `ListProductsAsync_paginates_via_cursor` — WireMock returns two pages with a `nextCursor`, assert the client surfaces them correctly.

These tests do **not** start a SearchWebAppFactory — they spin up just the DI container with the WireMock URL.

## Acceptance

```bash
dotnet build RitualworksPlatform.sln -c Release
dotnet test tests/Search.Integration -c Release --filter "FullyQualifiedName~CatalogProductsApiTests"
dotnet test tests/Search.Integration -c Release    # full suite still green
```

All green.

## Hard stops

- Do **not** call out to a real catalog instance from tests. WireMock only.
- Do **not** modify catalog code, even to "fix" a missing field on the response DTO. File a blocker if the response shape doesn't match the spec.
- Do **not** add a consumer or an endpoint here.
- Do **not** instantiate Polly policies inline; use `IResiliencePolicyFactory`.
- Do **not** add a generic `IHttpClientFactory` registration — Refit's `AddRefitClient` covers it.

## Done-report

Standard format. Confirm:
- 4 new tests pass.
- Refit client uses `IResiliencePolicyFactory`, not inline Polly.
- The DTO field names exactly match what catalog's controller returns (call out the controller path you verified against).
