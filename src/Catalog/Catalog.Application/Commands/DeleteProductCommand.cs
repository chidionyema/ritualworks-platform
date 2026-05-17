using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Messaging;
using Haworks.Catalog.Application.Interfaces;
using Haworks.Catalog.Domain.Interfaces;
using Haworks.Contracts.Catalog;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record DeleteProductCommand(
    Guid ProductId,
    Guid? CorrelationId = null) : IRequest<Result>;

internal sealed class DeleteProductCommandHandler : IRequestHandler<DeleteProductCommand, Result>
{
    private readonly IProductRepository _repository;
    private readonly IProductCacheReader _productCache;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly ILogger<DeleteProductCommandHandler> _logger;

    public DeleteProductCommandHandler(
        IProductRepository repository,
        IProductCacheReader productCache,
        IDomainEventPublisher eventPublisher,
        ILogger<DeleteProductCommandHandler> logger)
    {
        _repository = repository;
        _productCache = productCache;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task<Result> Handle(
        DeleteProductCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Deleting product {ProductId}", request.ProductId);

        var product = await _repository.GetByIdAsync(request.ProductId, ct: cancellationToken);
        if (product == null)
        {
            return Result.Failure(new Error("Products.NotFound", $"Product {request.ProductId} not found"));
        }

        try
        {
            await _repository.DeleteAsync(request.ProductId, cancellationToken);

            // Publish before SaveChanges so the outbox row commits with the
            // delete; bridge consumer fires only after durable commit.
            await _eventPublisher.PublishAsync(new ProductCacheInvalidatedEvent
            {
                ProductId = request.ProductId,
                CorrelationId = request.CorrelationId,
                Reason = "deleted",
                NewVersion = null,
            }, cancellationToken);

            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Product was already deleted by another request — treat as success
            _logger.LogInformation("Product {ProductId} was already deleted (concurrency)", request.ProductId);
        }

        await _productCache.InvalidateAsync(request.ProductId, cancellationToken);

        _logger.LogInformation("Product deleted {ProductId}", request.ProductId);

        return Result.Success();
    }
}
