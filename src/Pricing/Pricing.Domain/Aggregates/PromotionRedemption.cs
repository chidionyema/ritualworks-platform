using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Pricing.Domain.Aggregates;

public class PromotionRedemption : AuditableEntity
{
    protected PromotionRedemption() : base() { }

    private PromotionRedemption(Guid promotionId, Guid orderId, Guid userId)
    {
        PromotionId = promotionId;
        OrderId = orderId;
        UserId = userId;
        RedeemedAt = DateTime.UtcNow;
    }

    public Guid PromotionId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime RedeemedAt { get; private set; }

    public static PromotionRedemption Create(Guid promotionId, Guid orderId, Guid userId)
    {
        return new PromotionRedemption(promotionId, orderId, userId);
    }
}
