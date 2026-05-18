using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Application.Helpers;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;

namespace Haworks.Catalog.Application.Commands;

public sealed record UpdateProductReviewCommand(
    Guid ProductId,
    Guid ReviewId,
    string Title,
    string Content,
    int Rating,
    string? UserId,
    bool IsAdmin,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result<ProductReviewDto>>;

internal sealed class UpdateProductReviewCommandHandler
    : IRequestHandler<UpdateProductReviewCommand, Result<ProductReviewDto>>
{
    private readonly IProductReviewRepository _repository;

    public UpdateProductReviewCommandHandler(
        IProductReviewRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<ProductReviewDto>> Handle(
        UpdateProductReviewCommand request,
        CancellationToken cancellationToken)
    {
        var review = await _repository.GetByIdAsync(request.ReviewId, cancellationToken);
        if (review == null)
        {
            return Result.Failure<ProductReviewDto>(new Error("Reviews.NotFound", "Review not found"));
        }

        if (review.ProductId != request.ProductId)
        {
            return Result.Failure<ProductReviewDto>(new Error("Reviews.InvalidProduct", "Review does not belong to this product"));
        }

        // Security check
        if (!string.Equals(review.UserId, request.UserId, StringComparison.Ordinal) && !request.IsAdmin)
        {
            return Result.Failure<ProductReviewDto>(new Error("Reviews.Forbidden", "Not authorized to update this review"));
        }

        // Sanitize user input to prevent XSS
        var sanitizedTitle = TextSanitizer.SanitizePlainText(request.Title);
        var sanitizedContent = TextSanitizer.SanitizeMultilineText(request.Content);

        // Update mutable fields using the behavior method
        review.Update(request.Rating, sanitizedContent, sanitizedTitle);

        await _repository.UpdateAsync(review, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        var reviewDto = new ProductReviewDto(
            review.Id,
            review.ProductId,
            review.UserId,
            review.AuthorName,
            review.Title,
            review.Body,
            review.Rating,
            review.IsApproved,
            review.CreatedAt,
            review.LastModifiedDate);

        return Result.Success(reviewDto);
    }
}
