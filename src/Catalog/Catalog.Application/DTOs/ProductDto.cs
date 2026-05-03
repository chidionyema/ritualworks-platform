namespace Haworks.Catalog.Application.DTOs;

public sealed record ProductDto(
    Guid Id,
    string Name,
    string Description,
    decimal UnitPrice,
    int StockQuantity,
    bool IsInStock,
    bool IsListed,
    Guid CategoryId,
    string? CategoryName);

public sealed record CategoryDto(
    Guid Id,
    string Name,
    string? Description);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);
