using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Payouts.Domain.Aggregates;

public sealed class SellerProfile : AuditableEntity
{
    public required Guid SellerId { get; init; }
    public string? ExternalProviderId { get; set; } // Stripe Connect ID
    public string? KycStatus { get; set; }
    public bool PayoutsEnabled { get; set; }
    public string PayoutSchedule { get; set; } = "Monthly"; // daily, weekly, monthly, threshold
    public decimal PayoutThreshold { get; set; } = 50.00m;
    public decimal CommissionPercentage { get; set; } = 10.00m;

    public static SellerProfile Create(Guid sellerId)
    {
        return new SellerProfile
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            PayoutsEnabled = false,
            KycStatus = "Pending"
        };
    }
}
