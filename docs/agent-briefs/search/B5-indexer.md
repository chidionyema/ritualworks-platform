> **Note:** This brief was written when the plan was Meilisearch. The actual implementation uses **Elasticsearch 8**. References to Meilisearch below are historical.

# B5 — Indexer (consumers + projector)

## Goal

Two MassTransit consumers in `Search.Application` that react to catalog events and push updates to Meilisearch via `ISearchIndex`. Includes the Product → ProductSearchDocument projector and the OOO version guard.

(A third consumer for category-delete is deferred — the platform has no `DeleteCategoryCommand` and therefore no `CategoryDeletedEvent` per the spec.)

## Phase / blocks-on

Phase 3. Blocks-on: **B2 (ISearchIndex) AND B3 (CategoryUpdatedEvent contract record) AND B4 (ICatalogProductsApi)** all green.

## Inputs (read in order)

1. `docs/agent-briefs/search/README.md`.
2. `docs/agent-briefs/search-service-spec.md` §5 (indexer pipeline — every step), §4 (document shape — exact field names).
3. `src/Search/Search.Application/Interfaces/ISearchIndex.cs` (B2).
4. `src/Search/Search.Infrastructure/Catalog/ICatalogProductsApi.cs` (B4).
5. `src/Contracts/Catalog/ProductCacheInvalidatedEvent.cs` (existing) and the new `CategoryUpdatedEvent.cs` (B3).
6. `src/Payments/Payments.Application/Consumers/PaymentWebhookValidatedConsumer.cs` — the canonical consumer pattern (logging, retries, no try-swallow).
7. `tests/Payments.Integration/PaymentsWebAppFactory.cs` — fixture pattern. SearchWebAppFactory should mirror it: env-vars-before-build, MassTransit test harness, Testcontainers Meili.

## Deliverable

### Consumers

`src/Search/Search.Application/Consumers/ProductCacheInvalidatedConsumer.cs`:

- Inject `ICatalogProductsApi`, `ISearchIndex`, `ILogger<…>`.
- On `ProductCacheInvalidatedEvent`:
  - If `Reason == "deleted"` → `await searchIndex.DeleteAsync(productIdKey, ct)` and return.
  - Else → `var dto = await catalogApi.GetProductAsync(message.ProductId, ct);`. If 404, log a warning and return (product was deleted between event publish and our consume).
  - Run the OOO version guard from spec §5: `var existing = await searchIndex.GetAsync(productIdKey, ct); if (existing != null && existing.SourceVersion >= message.NewVersion) return;`.
  - Project DTO → `ProductSearchDocument` via the projector below.
  - `await searchIndex.UpsertAsync([doc], ct)`.

`src/Search/Search.Application/Consumers/CategoryUpdatedConsumer.cs`:

- On `CategoryUpdatedEvent`:
  - Page through Meilisearch with `searchIndex.SearchAsync(new SearchQuery { Filter = $"categoryId = {e.CategoryId}", PageSize = 1000 }, ct)`.
  - For each page, build documents with `categoryName = e.Name`, batch upsert.
  - Stop when no more hits.

### Projector

`src/Search/Search.Application/Indexing/ProductSearchDocumentProjector.cs`:

```csharp
public static class ProductSearchDocumentProjector
{
    public static ProductSearchDocument From(CatalogProductDto product, long sourceVersion)
        => new()
        {
            ProductIdKey = product.Id.ToString("N"),
            ProductId    = product.Id.ToString(),
            Name         = product.Name ?? "",
            Description  = product.Description ?? "",
            CategoryId   = product.Category?.Id.ToString() ?? Guid.Empty.ToString(),
            CategoryName = product.Category?.Name ?? "Uncategorized",
            UnitPrice    = product.UnitPrice,
            IsInStock    = product.IsInStock,
            IsListed     = product.IsListed,
            SourceVersion= sourceVersion,
            IndexedAt    = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
}
```

(Adjust field accesses to match the actual `CatalogProductDto` shape from B4.)

### Consumer registration

In `Search.Infrastructure.DependencyInjection.AddInfrastructure(...)`:

```csharp
if (!env.IsEnvironment("Test"))
{
    services.AddMassTransit(mt =>
    {
        mt.SetKebabCaseEndpointNameFormatter();
        mt.AddConsumer<ProductCacheInvalidatedConsumer>();
        mt.AddConsumer<CategoryUpdatedConsumer>();
        mt.UsingRabbitMq((ctx, cfg) =>
        {
            cfg.Host(new Uri(configuration.GetConnectionString("rabbitmq")
                ?? throw new InvalidOperationException()));
            cfg.ConfigureEndpoints(ctx);
        });
    });
}
```

(Match the `if (!env.IsEnvironment("Test"))` skip pattern from `Payments.Infrastructure.DependencyInjection`. Test harness wires its own MassTransit.)

### Tests

`tests/Search.Unit/ProductSearchDocumentProjectorTests.cs`:
- `From_maps_all_fields`
- `From_maps_null_category_to_uncategorized`
- `From_uses_dash_free_uuid_for_PrimaryKey`

`tests/Search.Integration/IndexerTests.cs` (using SearchWebAppFactory which now also spins Meili + a WireMock catalog stub):

- `ProductCacheInvalidated_with_Reason_updated_upserts_document`
- `ProductCacheInvalidated_with_Reason_deleted_removes_document`
- `ProductCacheInvalidated_with_lower_SourceVersion_is_a_noop` (seed a doc with version 10, publish event with version 5, assert doc unchanged)
- `CategoryUpdated_renames_category_for_all_products` (seed 3 products in same category, publish CategoryUpdatedEvent, assert all 3 updated)

Each integration test uses `harness.Bus.Publish(...)` then polls Meilisearch for the expected state with a 30s deadline, mirroring the `PollUntilAsync` pattern from WebhookFlowsTests.

## Acceptance

```bash
dotnet build RitualworksPlatform.sln -c Release
dotnet test tests/Search.Unit -c Release
dotnet test tests/Search.Integration -c Release
```

All green. The 4 new integration tests + 3 new unit tests are listed as passed.

## Hard stops

- Do **not** add the `/search` endpoint — that's B6.
- Do **not** modify B4's `ICatalogProductsApi` surface area. If you need a new method, file a blocker.
- Do **not** modify B2's `ISearchIndex` surface area. Same.
- Do **not** swallow exceptions in consumers. Re-throw so MT retries.
- Do **not** persist anything to Postgres — search-svc has no DbContext in v1.
- Do **not** add a backfill endpoint — that's deferred to phase 5.

## Done-report

Standard format. Confirm:
- 4 integration + 3 unit tests new and green.
- OOO guard verified via the `lower_SourceVersion_is_a_noop` test.
- Both consumers registered behind the `!env.IsEnvironment("Test")` guard.
