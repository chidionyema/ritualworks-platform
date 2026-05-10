using Haworks.BuildingBlocks.Persistence;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Domain.Aggregates;

public class Promotion : AuditableEntity
{
    private readonly List<PromotionRule> _rules = new();
    private readonly List<PromotionRedemption> _redemptions = new();

    protected Promotion() : base() { }

    private Promotion(string name, string description, DiscountType discountType, decimal discountValue, DateTime startDate, DateTime endDate)
    {
        Name = name;
        Description = description;
        DiscountType = discountType;
        DiscountValue = discountValue;
        StartDate = startDate;
        EndDate = endDate;
        IsActive = true;
    }

    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public bool IsActive { get; private set; }

    public IReadOnlyCollection<PromotionRule> Rules => _rules.AsReadOnly();
    public IReadOnlyCollection<PromotionRedemption> Redemptions => _redemptions.AsReadOnly();

    public static Promotion Create(string name, string description, DiscountType discountType, decimal discountValue, DateTime startDate, DateTime endDate)
    {
        return new Promotion(name, description, discountType, discountValue, startDate, endDate);
    }

    public void AddRule(RuleType type, string targetValue)
    {
        _rules.Add(PromotionRule.Create(Id, type, targetValue));
    }

    public void RecordRedemption(Guid orderId, Guid userId)
    {
        _redemptions.Add(PromotionRedemption.Create(Id, orderId, userId));
    }
}
