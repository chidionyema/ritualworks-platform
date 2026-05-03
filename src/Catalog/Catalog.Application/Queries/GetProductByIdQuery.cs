using MediatR;
using Haworks.Catalog.Application.DTOs;

namespace Haworks.Catalog.Application.Queries;

public sealed record GetProductByIdQuery(Guid Id) : IRequest<Result<ProductDto>>;

internal sealed class GetProductByIdQueryHandler(IProductRepository products)
    : IRequestHandler<GetProductByIdQuery, Result<ProductDto>>
{
    public async Task<Result<ProductDto>> Handle(GetProductByIdQuery request, CancellationToken ct)
    {
        var product = await products.GetByIdAsync(request.Id, ct);
        if (product is null)
        {
            return Result.Failure<ProductDto>(Error.Products.NotFoundWithId(request.Id));
        }

        return Result.Success(new ProductDto(
            product.Id,
            product.Name,
            product.Description,
            product.UnitPrice,
            product.StockQuantity,
            product.IsInStock,
            product.IsListed,
            product.CategoryId,
            product.Category?.Name));
    }
}
