using MediatR;

namespace Haworks.Pricing.Application.Commands;

/// <summary>
/// Validates a promotion code without redeeming it.
/// </summary>
public sealed record ValidatePromotionCodeQuery : IRequest<ValidatePromotionCodeResult>
{
    public required string Code { get; init; }
    public required Guid ProductId { get; init; }
    public string? UserId { get; init; }
}

/// <summary>
/// Result of promotion code validation.
/// </summary>
public sealed record ValidatePromotionCodeResult
{
    public bool Valid { get; init; }
    public string? DiscountType { get; init; }
    public decimal? Value { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Reason { get; init; }
}
