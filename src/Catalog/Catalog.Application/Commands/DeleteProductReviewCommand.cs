using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;

namespace Haworks.Catalog.Application.Commands;

public sealed record DeleteProductReviewCommand(
    Guid ProductId,
    Guid ReviewId,
    string? UserId,
    bool IsAdmin,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result>;

internal sealed class DeleteProductReviewCommandHandler : IRequestHandler<DeleteProductReviewCommand, Result>
{
    private readonly IProductReviewRepository _repository;

    public DeleteProductReviewCommandHandler(
        IProductReviewRepository repository)
    {
        _repository = repository;
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
        if (!string.Equals(review.UserId, request.UserId, StringComparison.Ordinal) && !request.IsAdmin)
        {
            return Result.Failure(new Error("Reviews.Forbidden", "Not authorized to delete this review"));
        }

        await _repository.DeleteAsync(request.ReviewId, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        
        return Result.Success();
    }
}
