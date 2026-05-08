namespace Haworks.Search.Infrastructure.Catalog;

/// <summary>
/// Catalog's offset-paginated list response. Mirrors
/// <c>Haworks.BuildingBlocks.Common.PagedResult&lt;ProductDto&gt;</c>.
/// </summary>
public sealed record CatalogProductPage(
    IReadOnlyList<CatalogProductDto> Items,
    int Total,
    int Skip,
    int Take);
