using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Pricing.Domain.Entities;

/// <summary>
/// Records a single redemption of a promotion code. Idempotency key: one redemption per order.
/// </summary>
public sealed class PromotionRedemption : AuditableEntity
{
    private PromotionRedemption() { }

    public Guid PromotionCodeId { get; private set; }
    public Guid OrderId { get; private set; }
    public string? UserId { get; private set; }
    public DateTimeOffset RedeemedAt { get; private set; }
    public decimal DiscountAmountApplied { get; private set; }

    public static PromotionRedemption Create(
        Guid promotionCodeId,
        Guid orderId,
        string? userId,
        decimal discountAmountApplied)
    {
        return new PromotionRedemption
        {
            PromotionCodeId = promotionCodeId,
            OrderId = orderId,
            UserId = userId,
            RedeemedAt = DateTimeOffset.UtcNow,
            DiscountAmountApplied = discountAmountApplied,
        };
    }
}
