using Haworks.BuildingBlocks.Persistence;
using Haworks.Payouts.Domain.Enums;

namespace Haworks.Payouts.Domain.Aggregates;

public sealed class LedgerAccount : AuditableEntity
{
    public required Guid OwnerId { get; init; } // System Guid or Seller Guid
    public required AccountType Type { get; init; }
    public required string Currency { get; init; }
    public decimal Balance { get; private set; }

    public void UpdateBalance(decimal amount, EntryType entryType)
    {
        if (amount < 0) throw new ArgumentException("Amount must be non-negative", nameof(amount));

        if (entryType == EntryType.Credit)
        {
            Balance += amount;
        }
        else
        {
            if (Balance < amount)
            {
                throw new InvalidOperationException($"Insufficient balance in ledger account {Id}. Available: {Balance}, Required: {amount}");
            }
            Balance -= amount;
        }
    }

    public static LedgerAccount Create(Guid ownerId, AccountType type, string currency)
    {
        return new LedgerAccount
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            Type = type,
            Currency = currency,
            Balance = 0
        };
    }
}
