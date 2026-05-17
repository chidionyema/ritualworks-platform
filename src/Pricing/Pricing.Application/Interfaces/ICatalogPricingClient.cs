using Haworks.Pricing.Application.Models;

namespace Haworks.Pricing.Application.Interfaces;

/// <summary>
/// HTTP client for fetching product data from the Catalog service.
/// Implementation uses Refit in the Infrastructure layer.
/// </summary>
public interface ICatalogPricingClient
{
    Task<CatalogProductResponse?> GetProductAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Wrapper response from catalog client.
/// </summary>
public sealed record CatalogProductResponse
{
    public bool IsSuccess { get; init; }
    public CatalogProductDto? Product { get; init; }
}
