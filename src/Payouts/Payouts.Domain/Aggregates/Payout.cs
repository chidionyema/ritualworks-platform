using Haworks.BuildingBlocks.Persistence;
using Haworks.Payouts.Domain.Enums;

namespace Haworks.Payouts.Domain.Aggregates;

public sealed class Payout : AuditableEntity
{
    private static readonly PayoutStatus[] TerminalStatuses =
        [PayoutStatus.Succeeded, PayoutStatus.Failed, PayoutStatus.Cancelled];

    public required Guid SellerId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
    // L2 Fix: Persist idempotency key so recovery jobs can retry with the same key
    public string? IdempotencyKey { get; set; }
    public PayoutStatus Status { get; private set; }
    public string? ExternalReference { get; private set; }
    public string? TransitReference { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? ScheduledFor { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public void MarkInTransit(string externalReference, string? transitReference = null)
    {
        if (Status is not (PayoutStatus.Pending or PayoutStatus.Scheduled))
            throw new InvalidOperationException($"Cannot transition to InTransit from {Status}.");

        Status = PayoutStatus.InTransit;
        ExternalReference = externalReference;
        TransitReference = transitReference;
    }

    public void MarkSucceeded()
    {
        if (Status != PayoutStatus.InTransit)
            throw new InvalidOperationException($"Cannot transition to Succeeded from {Status}.");

        Status = PayoutStatus.Succeeded;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string reason)
    {
        if (Status is PayoutStatus.Succeeded or PayoutStatus.Failed or PayoutStatus.Cancelled)
            throw new InvalidOperationException($"Cannot transition to Failed from {Status}.");

        Status = PayoutStatus.Failed;
        FailureReason = reason;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public void MarkCancelled()
    {
        if (Status is not (PayoutStatus.Pending or PayoutStatus.Scheduled))
            throw new InvalidOperationException($"Cannot transition to Cancelled from {Status}.");

        Status = PayoutStatus.Cancelled;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public static Payout Create(Guid sellerId, decimal amount, string currency, DateTimeOffset? scheduledFor = null)
    {
        if (amount <= 0)
            throw new ArgumentException("Payout amount must be positive.", nameof(amount));

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw new ArgumentException("Currency must be a 3-letter uppercase ISO code.", nameof(currency));

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
