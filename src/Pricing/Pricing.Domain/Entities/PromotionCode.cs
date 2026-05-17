using Haworks.BuildingBlocks.Persistence;
using Haworks.Pricing.Domain.Enums;

namespace Haworks.Pricing.Domain.Entities;

/// <summary>
/// Promotion code aggregate root. Supports percentage and fixed-amount discounts
/// with max-uses and per-user limits.
/// </summary>
public sealed class PromotionCode : AuditableEntity
{
    private readonly List<PromotionRedemption> _redemptions = new();

    private PromotionCode() { }

    public string Code { get; private set; } = string.Empty;
    public DiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }
    public decimal? MinimumOrderAmount { get; private set; }
    public Guid? ApplicableProductId { get; private set; }
    public Guid? ApplicableCategoryId { get; private set; }
    public int? MaxUses { get; private set; }
    public int UsesCount { get; private set; }
    public int? MaxUsesPerUser { get; private set; }
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public string SellerTimezone { get; private set; } = "America/New_York";
    public bool IsActive { get; private set; }
    public bool IsDeleted { get; private set; }

    public IReadOnlyList<PromotionRedemption> Redemptions => _redemptions.AsReadOnly();

    /// <summary>
    /// Factory method enforcing domain invariants.
    /// </summary>
    public static PromotionCode Create(
        string code,
        DiscountType discountType,
        decimal discountValue,
        decimal? minimumOrderAmount = null,
        Guid? applicableProductId = null,
        Guid? applicableCategoryId = null,
        int? maxUses = null,
        int? maxUsesPerUser = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        string sellerTimezone = "America/New_York")
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Code cannot be empty.");

        if (code.Length > 32)
            throw new ArgumentException("Code cannot exceed 32 characters.");

        if (discountValue <= 0)
            throw new ArgumentException("DiscountValue must be greater than 0.");

        if (discountType == DiscountType.Percentage && discountValue > 100)
            throw new ArgumentException("Percentage discount cannot exceed 100.");

        if (startsAt.HasValue && expiresAt.HasValue && expiresAt.Value <= startsAt.Value)
            throw new ArgumentException("ExpiresAt must be after StartsAt.");

        return new PromotionCode
        {
            Code = code.ToUpperInvariant(),
            DiscountType = discountType,
            DiscountValue = discountValue,
            MinimumOrderAmount = minimumOrderAmount,
            ApplicableProductId = applicableProductId,
            ApplicableCategoryId = applicableCategoryId,
            MaxUses = maxUses,
            MaxUsesPerUser = maxUsesPerUser,
            StartsAt = startsAt,
            ExpiresAt = expiresAt,
            SellerTimezone = sellerTimezone,
            IsActive = true,
            IsDeleted = false,
            UsesCount = 0,
        };
    }

    /// <summary>
    /// Validates if this code can be redeemed at the given time.
    /// Does NOT check MaxUses atomically — that is done via CAS UPDATE in the repository.
    /// </summary>
    public bool CanRedeem(DateTimeOffset at)
    {
        if (!IsActive || IsDeleted) return false;
        if (StartsAt.HasValue && at < StartsAt.Value) return false;
        if (ExpiresAt.HasValue && at >= ExpiresAt.Value) return false;
        if (MaxUses.HasValue && UsesCount >= MaxUses.Value) return false;
        return true;
    }

    /// <summary>
    /// Checks if the code is applicable to the given product/category.
    /// </summary>
    public bool IsApplicableTo(Guid productId, Guid? categoryId)
    {
        if (ApplicableProductId.HasValue && ApplicableProductId.Value != productId)
            return false;
        if (ApplicableCategoryId.HasValue && categoryId.HasValue && ApplicableCategoryId.Value != categoryId.Value)
            return false;
        return true;
    }

    /// <summary>
    /// Soft-delete. In-flight redemptions are honoured.
    /// </summary>
    public void SoftDelete()
    {
        IsActive = false;
        IsDeleted = true;
        LastModifiedDate = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Increment uses count. Called after successful CAS UPDATE.
    /// </summary>
    public void IncrementUsesCount()
    {
        UsesCount++;
        LastModifiedDate = DateTimeOffset.UtcNow;
    }
}
