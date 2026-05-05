using Haworks.Catalog.Application.DTOs;

namespace Haworks.Catalog.Application.Interfaces;

/// <summary>
/// Read-through cache over the real <c>IProductRepository.GetByIdAsync</c>
/// using <c>HybridCache</c>. Backing repository is the same EF-against-Postgres
/// implementation production uses; this layer adds an in-process L1 with
/// HybridCache's built-in stampede protection.
///
/// Mutations (Update, Delete, ReserveStock) MUST call
/// <see cref="InvalidateAsync"/> after their save commits, otherwise readers
/// will see stale data until the cache TTL expires.
/// </summary>
public interface IProductCacheReader
{
    /// <summary>
    /// Read a product by id. Returns the DTO, the source it came from
    /// (<c>"L1"</c> for in-process cache hit, <c>"database"</c> for a
    /// cache miss that hit Postgres, <c>"not_found"</c> for missing),
    /// and the wall-clock latency the call observed.
    /// </summary>
    Task<ProductCacheReadResult> GetAsync(Guid productId, CancellationToken ct);

    /// <summary>
    /// Remove the product from the in-process cache. Idempotent — safe
    /// to call when the key isn't present.
    /// </summary>
    Task InvalidateAsync(Guid productId, CancellationToken ct);
}

public sealed record ProductCacheReadResult(
    ProductDto? Product,
    string Source,
    long LatencyMs);
