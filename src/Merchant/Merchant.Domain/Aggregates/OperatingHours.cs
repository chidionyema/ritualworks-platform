using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Merchant.Domain.Aggregates;

public sealed class OperatingHours : AuditableEntity
{
    public required Guid MerchantId { get; init; }
    public required int DayOfWeek { get; init; } // 0-6
    public required TimeSpan OpenTime { get; set; }
    public required TimeSpan CloseTime { get; set; }
    public bool IsOpen { get; private set; } = true;

    public void Close() => IsOpen = false;
    public void Open() => IsOpen = true;

    public static OperatingHours Create(Guid merchantId, int dayOfWeek, TimeSpan openTime, TimeSpan closeTime, bool isOpen = true)
    {
        return new OperatingHours
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            DayOfWeek = dayOfWeek,
            OpenTime = openTime,
            CloseTime = closeTime,
            IsOpen = isOpen
        };
    }
}
