using Haworks.BuildingBlocks.Common;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Commands;

public sealed record ApproveProductReviewCommand(
    Guid ProductId,
    Guid ReviewId
) : IRequest<Result>;

internal sealed class ApproveProductReviewCommandHandler : IRequestHandler<ApproveProductReviewCommand, Result>
{
    private readonly IProductReviewRepository _repository;
    private readonly ILogger<ApproveProductReviewCommandHandler> _logger;

    public ApproveProductReviewCommandHandler(
        IProductReviewRepository repository,
        ILogger<ApproveProductReviewCommandHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<Result> Handle(ApproveProductReviewCommand request, CancellationToken cancellationToken)
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

        if (review.IsApproved)
        {
            return Result.Success(); // Already approved
        }

        review.Approve();
        await _repository.UpdateAsync(review, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);
        
        return Result.Success();
    }
}
