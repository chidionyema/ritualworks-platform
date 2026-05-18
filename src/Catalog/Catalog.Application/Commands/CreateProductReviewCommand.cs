using Haworks.BuildingBlocks.Common;
using Haworks.BuildingBlocks.Idempotency;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Application.Helpers;
using Haworks.Catalog.Domain;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;

namespace Haworks.Catalog.Application.Commands;

public sealed record CreateProductReviewCommand(
    Guid ProductId,
    string Title,
    string Content,
    int Rating,
    string UserId,
    string? AuthorName,
    bool IsAdmin,
    string IdempotencyKey = ""
) : IIdempotentCommand, IRequest<Result<ProductReviewDto>>;

internal sealed class CreateProductReviewCommandHandler
    : IRequestHandler<CreateProductReviewCommand, Result<ProductReviewDto>>
{
    private readonly IProductRepository _productRepository;
    private readonly IProductReviewRepository _reviewRepository;

    public CreateProductReviewCommandHandler(
        IProductRepository productRepository,
        IProductReviewRepository reviewRepository)
    {
        _productRepository = productRepository;
        _reviewRepository = reviewRepository;
    }

    public async Task<Result<ProductReviewDto>> Handle(
        CreateProductReviewCommand request,
        CancellationToken cancellationToken)
    {
        var product = await _productRepository.GetByIdAsync(request.ProductId, ct: cancellationToken);
        if (product == null)
        {
            return Result.Failure<ProductReviewDto>(new Error("Reviews.ProductNotFound", "Product not found"));
        }

        if (string.IsNullOrEmpty(request.UserId))
        {
            return Result.Failure<ProductReviewDto>(new Error("Reviews.CreateFailed", "Invalid user ID"));
        }

        // Sanitize user input to prevent XSS
        var sanitizedTitle = TextSanitizer.SanitizePlainText(request.Title);
        var sanitizedContent = TextSanitizer.SanitizeMultilineText(request.Content);
        var sanitizedAuthorName = TextSanitizer.SanitizePlainText(request.AuthorName);

        var review = ProductReview.Create(
            request.ProductId,
            request.UserId,
            request.Rating,
            sanitizedContent,
            sanitizedAuthorName,
            sanitizedTitle);

        if (request.IsAdmin)
        {
            review.Approve();
        }

        await _reviewRepository.AddAsync(review, cancellationToken);
        await _reviewRepository.SaveChangesAsync(cancellationToken);

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
