using Haworks.BuildingBlocks.Idempotency;
using Haworks.Pricing.Domain.Enums;
using MediatR;

namespace Haworks.Pricing.Application.Commands;

/// <summary>
/// Creates a new pricing rule (admin only).
/// </summary>
public sealed record CreatePriceRuleCommand : IIdempotentCommand, IRequest<Guid>
{
    public Guid? ProductId { get; init; }
    public Guid? CategoryId { get; init; }
    public int Priority { get; init; }
    public DiscountType DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public int MinimumQuantity { get; init; }
    public int? MaximumQuantity { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string SellerTimezone { get; init; } = "America/New_York";
    public string IdempotencyKey { get; init; } = string.Empty;
}
