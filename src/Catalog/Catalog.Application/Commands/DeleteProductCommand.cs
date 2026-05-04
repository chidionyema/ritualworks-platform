using Haworks.BuildingBlocks.Common;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record DeleteProductCommand(Guid ProductId) : IRequest<Result>;

internal sealed class DeleteProductCommandHandler : IRequestHandler<DeleteProductCommand, Result>
{
    private readonly IProductRepository _repository;
    private readonly ILogger<DeleteProductCommandHandler> _logger;

    public DeleteProductCommandHandler(
        IProductRepository repository,
        ILogger<DeleteProductCommandHandler> logger)
    {
        _repository = repository;
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

        await _repository.DeleteAsync(request.ProductId, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Product deleted {ProductId}", request.ProductId);

        return Result.Success();
    }
}
