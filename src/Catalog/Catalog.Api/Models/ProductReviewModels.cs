namespace Haworks.Catalog.Api.Models;

public sealed record CreateProductReviewRequest(
    string Title,
    string Content,
    int Rating,
    string? AuthorName);

public sealed record ProductReviewResponse(
    Guid Id,
    Guid ProductId,
    string UserId,
    string? AuthorName,
    string? Title,
    string? Body,
    int Rating,
    bool IsApproved,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
