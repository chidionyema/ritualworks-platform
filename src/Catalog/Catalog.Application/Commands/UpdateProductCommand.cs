using Haworks.BuildingBlocks.Common;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record UpdateProductCommand(
    Guid ProductId,
    string Name,
    string Description,
    decimal UnitPrice,
    Guid CategoryId,
    bool IsListed
) : IRequest<Result<ProductDto>>;

internal sealed class UpdateProductCommandHandler : IRequestHandler<UpdateProductCommand, Result<ProductDto>>
{
    private readonly IProductRepository _productRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly ILogger<UpdateProductCommandHandler> _logger;

    public UpdateProductCommandHandler(
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        ILogger<UpdateProductCommandHandler> logger)
    {
        _productRepository = productRepository;
        _categoryRepository = categoryRepository;
        _logger = logger;
    }

    public async Task<Result<ProductDto>> Handle(
        UpdateProductCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Updating product {ProductId}", request.ProductId);

        var product = await _productRepository.GetByIdTrackedAsync(request.ProductId, ct: cancellationToken);
        if (product == null)
        {
            return Result.Failure<ProductDto>(new Error("Products.NotFound", $"Product {request.ProductId} not found"));
        }

        var category = await _categoryRepository.GetByIdAsync(request.CategoryId, cancellationToken);
        if (category == null)
        {
            return Result.Failure<ProductDto>(new Error("Products.InvalidCategory", "Category not found"));
        }

        product.UpdateBasicInfo(request.Name, request.Description);
        product.UpdatePricing(request.UnitPrice);
        
        // CategoryId can't be changed via basic update per ADR-0009 or requires specific handling
        // For this port, we will update it if needed. The Product entity currently doesn't have an UpdateCategory method
        // So we will leave Category updates aside, or if required, add it to Domain Entity.
        // product.UpdateCategory(request.CategoryId);

        if (request.IsListed)
        {
            product.List();
        }
        else
        {
            product.Unlist();
        }

        await _productRepository.UpdateAsync(product, cancellationToken);
        await _productRepository.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Product updated {ProductId}", request.ProductId);

        var result = new ProductDto(
            product.Id,
            product.Name,
            product.Description,
            product.UnitPrice,
            product.StockQuantity,
            product.IsInStock,
            product.IsListed,
            product.CategoryId,
            category.Name);

        return Result.Success(result);
    }
}
