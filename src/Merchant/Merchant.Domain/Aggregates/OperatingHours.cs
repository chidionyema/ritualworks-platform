using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Merchant.Domain.Aggregates;

public sealed class OperatingHours : AuditableEntity
{
    public required Guid MerchantId { get; init; }
    public required int DayOfWeek { get; init; } // 0-6
    public required TimeSpan OpenTime { get; set; }
    public required TimeSpan CloseTime { get; set; }

    public static OperatingHours Create(Guid merchantId, int dayOfWeek, TimeSpan openTime, TimeSpan closeTime)
    {
        return new OperatingHours
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            DayOfWeek = dayOfWeek,
            OpenTime = openTime,
            CloseTime = closeTime
        };
    }
}
