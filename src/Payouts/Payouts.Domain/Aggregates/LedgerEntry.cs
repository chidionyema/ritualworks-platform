using Haworks.BuildingBlocks.Persistence;
using Haworks.Payouts.Domain.Enums;

namespace Haworks.Payouts.Domain.Aggregates;

public sealed class LedgerEntry : AuditableEntity
{
    public required Guid AccountId { get; init; }
    public required Guid TransactionId { get; init; }
    public required decimal Amount { get; init; }
    public required EntryType Type { get; init; }
    public required string Description { get; init; }
    public required string ReferenceId { get; init; } // External reference (Order, Payout ID)

    public static LedgerEntry Create(Guid accountId, Guid transactionId, decimal amount, EntryType type, string description, string referenceId)
    {
        return new LedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            TransactionId = transactionId,
            Amount = amount,
            Type = type,
            Description = description,
            ReferenceId = referenceId
        };
    }
}
