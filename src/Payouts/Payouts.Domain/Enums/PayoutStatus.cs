namespace Haworks.Payouts.Domain.Enums;

public enum PayoutStatus
{
    Pending,
    Scheduled,
    InTransit,
    Succeeded,
    Failed,
    Cancelled
}
