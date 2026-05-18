using Haworks.BuildingBlocks.Idempotency;
using Haworks.Pricing.Domain.Enums;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

/// <summary>
/// Creates a new promotion code (admin only).
/// </summary>
public sealed record CreatePromotionCodeCommand : IIdempotentCommand, IRequest<Guid>
{
    public required string Code { get; init; }
    public DiscountType DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public decimal? MinimumOrderAmount { get; init; }
    public Guid? ApplicableProductId { get; init; }
    public Guid? ApplicableCategoryId { get; init; }
    public int? MaxUses { get; init; }
    public int? MaxUsesPerUser { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string SellerTimezone { get; init; } = "America/New_York";
    public string IdempotencyKey { get; init; } = string.Empty;
}
