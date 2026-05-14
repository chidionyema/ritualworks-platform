using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Ledger.Services;

public interface ILedgerService
{
    Task CreditSellerAsync(Guid sellerId, decimal amount, string currency, Guid referenceId, string description);
    Task<decimal> GetBalanceAsync(Guid sellerId, AccountType type, string currency);
}

public class LedgerService : ILedgerService
{
    private readonly IPayoutsDbContext _context;
    private static readonly Guid SystemPlatformId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public LedgerService(IPayoutsDbContext context)
    {
        _context = context;
    }

    public async Task CreditSellerAsync(Guid sellerId, decimal amount, string currency, Guid referenceId, string description)
    {
        var transactionId = Guid.NewGuid();
        var sellerAccount = await GetOrCreateAccount(sellerId, AccountType.SellerPending, currency);
        var platformAccount = await GetOrCreateAccount(SystemPlatformId, AccountType.PlatformHolding, currency);

        var sellerEntry = LedgerEntry.Create(sellerAccount.Id, transactionId, amount, EntryType.Credit, description, referenceId.ToString());
        var platformEntry = LedgerEntry.Create(platformAccount.Id, transactionId, amount, EntryType.Debit, description, referenceId.ToString());

        sellerAccount.UpdateBalance(amount, EntryType.Credit);
        platformAccount.UpdateBalance(amount, EntryType.Debit);

        _context.LedgerEntries.AddRange(sellerEntry, platformEntry);
        await _context.SaveChangesAsync();
    }

    public async Task<decimal> GetBalanceAsync(Guid sellerId, AccountType type, string currency)
    {
        var account = await _context.LedgerAccounts
            .FirstOrDefaultAsync(a => a.OwnerId == sellerId && a.Type == type && a.Currency == currency);
        return account?.Balance ?? 0;
    }

    private async Task<LedgerAccount> GetOrCreateAccount(Guid ownerId, AccountType type, string currency)
    {
        var account = await _context.LedgerAccounts
            .FirstOrDefaultAsync(a => a.OwnerId == ownerId && a.Type == type && a.Currency == currency);

        if (account == null)
        {
            account = LedgerAccount.Create(ownerId, type, currency);
            _context.LedgerAccounts.Add(account);
            await _context.SaveChangesAsync();
        }

        return account;
    }
}
