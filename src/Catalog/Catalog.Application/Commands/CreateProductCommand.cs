using FluentValidation;
using MediatR;

namespace Haworks.Catalog.Application.Commands;

public sealed record CreateProductCommand(
    string Name,
    string Description,
    decimal UnitPrice,
    Guid CategoryId,
    int InitialStock) : IRequest<Result<Guid>>;


internal sealed class CreateProductCommandHandler(
    IProductRepository products,
    ICategoryRepository categories,
    ILogger<CreateProductCommandHandler> logger
) : IRequestHandler<CreateProductCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateProductCommand request, CancellationToken ct)
    {
        var category = await categories.GetByIdAsync(request.CategoryId, ct);
        if (category is null)
        {
            return Result.Failure<Guid>(Error.Categories.NotFoundWithId(request.CategoryId));
        }

        var product = Product.Create(request.Name, request.Description, request.UnitPrice, request.CategoryId);
        if (request.InitialStock > 0)
        {
            product.RestockTo(request.InitialStock);
        }
        product.List();

        await products.AddAsync(product, ct);
        await products.SaveChangesAsync(ct);

        logger.LogInformation("Product {ProductId} created in category {CategoryId}", product.Id, request.CategoryId);
        return Result.Success(product.Id);
    }
}
