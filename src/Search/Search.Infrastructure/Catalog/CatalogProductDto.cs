namespace Haworks.Search.Infrastructure.Catalog;

/// <summary>
/// Mirror of <c>Haworks.Catalog.Application.DTOs.ProductDto</c>. Search-svc
/// is not allowed to project across the contract boundary directly, so the
/// shape is duplicated here. <see cref="CategoryName"/> is null on the list
/// projection — only <c>GET /api/products/{id}</c> populates it. The
/// indexer enriches per-product via the get-by-id endpoint accordingly.
/// </summary>
public sealed record CatalogProductDto(
    Guid Id,
    string Name,
    string Description,
    decimal UnitPrice,
    int StockQuantity,
    bool IsInStock,
    bool IsListed,
    Guid CategoryId,
    string? CategoryName);
