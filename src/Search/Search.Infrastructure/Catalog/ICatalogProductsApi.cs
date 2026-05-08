namespace Haworks.Search.Infrastructure.Catalog;

/// <summary>
/// Typed catalog-service HTTP client. Read-only — search-svc never writes
/// to catalog. Implementations wrap calls in the platform's standard
/// resilience policy (retry + circuit breaker + bulkhead via
/// <c>ResilienceOptions.ForExternalApi("catalog")</c>).
/// </summary>
public interface ICatalogProductsApi
{
    /// <summary>
    /// Fetch a single enriched product (includes denormalised CategoryName).
    /// Throws on non-2xx after retries are exhausted.
    /// </summary>
    Task<CatalogProductDto> GetProductAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// List products with offset pagination, matching catalog's actual API.
    /// CategoryName on the returned items is null — backfill must enrich
    /// each via <see cref="GetProductAsync"/>.
    /// </summary>
    Task<CatalogProductPage> ListProductsAsync(int skip, int take, Guid? categoryId, CancellationToken ct = default);
}
