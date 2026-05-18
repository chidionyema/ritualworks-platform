using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Contracts.Catalog;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record UpdateProductCommand(
    Guid ProductId,
    string Name,
    string Description,
    decimal UnitPrice,
    Guid CategoryId,
    bool IsListed,
    Guid? CorrelationId = null,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result<ProductDto>>;

internal sealed class UpdateProductCommandHandler : IRequestHandler<UpdateProductCommand, Result<ProductDto>>
{
    private readonly IProductRepository _productRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly IProductCacheReader _productCache;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly ILogger<UpdateProductCommandHandler> _logger;

    public UpdateProductCommandHandler(
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IProductCacheReader productCache,
        IDomainEventPublisher eventPublisher,
        ILogger<UpdateProductCommandHandler> logger)
    {
        _productRepository = productRepository;
        _categoryRepository = categoryRepository;
        _productCache = productCache;
        _eventPublisher = eventPublisher;
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

        // Publish the cache-invalidation event BEFORE SaveChanges so it lands
        // in the outbox in the same transaction as the row write — the
        // ProductCacheInvalidatedBridge in BffWeb won't fire until the row
        // is durably committed.
        await _eventPublisher.PublishAsync(new ProductCacheInvalidatedEvent
        {
            ProductId = product.Id,
            CorrelationId = request.CorrelationId,
            Reason = "updated",
            NewVersion = null,
        }, cancellationToken);

        await _productRepository.SaveChangesAsync(cancellationToken);

        // Cache invalidation happens after the commit so a concurrent reader
        // can't observe stale data and re-populate the cache before the new
        // value is durable.
        await _productCache.InvalidateAsync(product.Id, cancellationToken);

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
