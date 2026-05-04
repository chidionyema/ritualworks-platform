using Haworks.BuildingBlocks.Common;
using Haworks.Catalog.Application.DTOs;
using Haworks.Catalog.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Haworks.Catalog.Application.Queries;

public sealed record GetProductReviewsQuery(
    Guid ProductId,
    int Skip = 0,
    int Take = 20
) : IRequest<Result<IReadOnlyList<ProductReviewDto>>>;

internal sealed class GetProductReviewsQueryHandler
    : IRequestHandler<GetProductReviewsQuery, Result<IReadOnlyList<ProductReviewDto>>>
{
    private readonly IProductRepository _productRepository;
    private readonly IProductReviewRepository _reviewRepository;
    private readonly ILogger<GetProductReviewsQueryHandler> _logger;

    public GetProductReviewsQueryHandler(
        IProductRepository productRepository,
        IProductReviewRepository reviewRepository,
        ILogger<GetProductReviewsQueryHandler> logger)
    {
        _productRepository = productRepository;
        _reviewRepository = reviewRepository;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<ProductReviewDto>>> Handle(
        GetProductReviewsQuery request,
        CancellationToken cancellationToken)
    {
        var product = await _productRepository.GetByIdAsync(request.ProductId, ct: cancellationToken);
        if (product == null)
        {
            return Result.Failure<IReadOnlyList<ProductReviewDto>>(new Error("Reviews.ProductNotFound", "Product not found"));
        }

        var reviews = await _reviewRepository.ListByProductIdAsync(request.ProductId, request.Skip, request.Take, cancellationToken);

        var reviewDtos = reviews.Select(review => new ProductReviewDto(
            review.Id,
            review.ProductId,
            review.UserId,
            review.AuthorName,
            review.Title,
            review.Body,
            review.Rating,
            review.IsApproved,
            review.CreatedAt,
            review.LastModifiedDate)).ToList();

        return Result.Success<IReadOnlyList<ProductReviewDto>>(reviewDtos);
    }
}
