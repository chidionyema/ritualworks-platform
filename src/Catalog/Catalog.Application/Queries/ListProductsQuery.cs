using MediatR;
using Haworks.Catalog.Application.DTOs;

namespace Haworks.Catalog.Application.Queries;

public sealed record ListProductsQuery(int Skip, int Take, Guid? CategoryId)
    : IRequest<Result<PagedResult<ProductDto>>>;

internal sealed class ListProductsQueryHandler(IProductRepository products)
    : IRequestHandler<ListProductsQuery, Result<PagedResult<ProductDto>>>
{
    private const int MaxPageSize = 100;

    public async Task<Result<PagedResult<ProductDto>>> Handle(ListProductsQuery request, CancellationToken ct)
    {
        var skip = Math.Max(0, request.Skip);
        var take = Math.Clamp(request.Take <= 0 ? 20 : request.Take, 1, MaxPageSize);

        var items = await products.ListAsync(skip, take, request.CategoryId, ct);
        var total = await products.CountAsync(request.CategoryId, ct);

        var dtos = items.Select(p => new ProductDto(
            p.Id, p.Name, p.Description, p.UnitPrice,
            p.StockQuantity, p.IsInStock, p.IsListed,
            p.CategoryId, null)).ToList();

        return Result.Success(new PagedResult<ProductDto>(dtos, total, skip, take));
    }
}
