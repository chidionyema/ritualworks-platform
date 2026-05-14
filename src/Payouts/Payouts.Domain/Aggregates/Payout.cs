using Haworks.BuildingBlocks.Persistence;
using Haworks.Payouts.Domain.Enums;

namespace Haworks.Payouts.Domain.Aggregates;

public sealed class Payout : AuditableEntity
{
    public required Guid SellerId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    public PayoutStatus Status { get; private set; }
    public string? ExternalReference { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? ScheduledFor { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public void MarkInTransit(string externalReference)
    {
        Status = PayoutStatus.InTransit;
        ExternalReference = externalReference;
    }

    public void MarkSucceeded()
    {
        Status = PayoutStatus.Succeeded;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        Status = PayoutStatus.Failed;
        FailureReason = reason;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public static Payout Create(Guid sellerId, decimal amount, string currency, DateTimeOffset? scheduledFor = null)
    {
        return new Payout
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            Amount = amount,
            Currency = currency,
            Status = PayoutStatus.Pending,
            ScheduledFor = scheduledFor ?? DateTimeOffset.UtcNow
        };
    }
}
