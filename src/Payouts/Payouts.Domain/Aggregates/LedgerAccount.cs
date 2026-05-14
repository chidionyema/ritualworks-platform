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
        if (entryType == EntryType.Credit)
            Balance += amount;
        else
        {
            if (Type is AccountType.SellerPending or AccountType.SellerPayable && Balance < amount)
                throw new InvalidOperationException("Insufficient balance");
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
