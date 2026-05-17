namespace Haworks.Pricing.Domain.Enums;

/// <summary>
/// Lifecycle status of a price rule.
/// </summary>
public enum PriceRuleStatus
{
    /// <summary>Rule is active and evaluated during pricing.</summary>
    Active = 0,

    /// <summary>Rule is scheduled but not yet active.</summary>
    Scheduled = 1,

    /// <summary>Rule has been soft-deleted (superseded).</summary>
    Archived = 2,
}
