namespace Haworks.Pricing.Application.Models;

/// <summary>
/// DTO returned by the Catalog service for pricing purposes.
/// </summary>
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
