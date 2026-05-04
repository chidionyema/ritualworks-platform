using Haworks.BuildingBlocks.Common;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record DeleteProductReviewCommand(
    Guid ProductId,
    Guid ReviewId,
    string? UserId,
    bool IsAdmin
) : IRequest<Result>;

internal sealed class DeleteProductReviewCommandHandler : IRequestHandler<DeleteProductReviewCommand, Result>
{
    private readonly IProductReviewRepository _repository;
    private readonly ILogger<DeleteProductReviewCommandHandler> _logger;

    public DeleteProductReviewCommandHandler(
        IProductReviewRepository repository,
        ILogger<DeleteProductReviewCommandHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result> Handle(DeleteProductReviewCommand request, CancellationToken cancellationToken)
    {
        var review = await _repository.GetByIdAsync(request.ReviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure(new Error("Reviews.NotFound", "Review not found"));
        }

        if (review.ProductId != request.ProductId)
        {
            return Result.Failure(new Error("Reviews.InvalidProduct", "Review does not belong to this product"));
        }

        // Security check
        if (review.UserId != request.UserId && !request.IsAdmin)
        {
            return Result.Failure(new Error("Reviews.Forbidden", "Not authorized to delete this review"));
        }

        await _repository.DeleteAsync(request.ReviewId, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        
        return Result.Success();
    }
}
