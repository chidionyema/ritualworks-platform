using Haworks.BuildingBlocks.Persistence;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Domain.Aggregates;

public class PromotionRule : AuditableEntity
{
    protected PromotionRule() : base() { }

    private PromotionRule(Guid promotionId, RuleType ruleType, string targetValue)
    {
        PromotionId = promotionId;
        RuleType = ruleType;
        TargetValue = targetValue;
    }

    public Guid PromotionId { get; private set; }
    public RuleType RuleType { get; private set; }
    public string TargetValue { get; private set; } = string.Empty;

    public static PromotionRule Create(Guid promotionId, RuleType ruleType, string targetValue)
    {
        return new PromotionRule(promotionId, ruleType, targetValue);
    }
}
