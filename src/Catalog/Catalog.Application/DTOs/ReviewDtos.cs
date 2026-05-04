namespace Haworks.Catalog.Application.DTOs;

public sealed record ProductReviewDto(
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
