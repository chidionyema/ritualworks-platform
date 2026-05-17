using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Payouts.Application.Ledger.Services;

public interface ILedgerService
{
    Task CreditSellerAsync(Guid sellerId, decimal amount, string currency, Guid referenceId, string description, CancellationToken ct = default);
    Task DebitSellerAsync(Guid sellerId, decimal amount, string currency, Guid referenceId, string description, CancellationToken ct = default);
    Task<decimal> GetBalanceAsync(Guid sellerId, AccountType type, string currency, CancellationToken ct = default);
    Task<bool> HasCreditForReferenceAsync(Guid referenceId, CancellationToken ct = default);
}

public class LedgerService : ILedgerService
{
    private readonly IPayoutsDbContext _context;
    private readonly ILogger<LedgerService> _logger;
    private static readonly Guid SystemPlatformId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public LedgerService(IPayoutsDbContext context, ILogger<LedgerService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Credits a seller's pending account after a payment completes.
    /// Double-entry: Credit PlatformHolding, Debit SellerPending + PlatformRevenue.
    ///
    /// Idempotent: checks if ReferenceId already has ledger entries before writing.
    /// Transactional: all accounts + entries commit atomically.
    /// </summary>
    public async Task CreditSellerAsync(Guid sellerId, decimal amount, string currency, Guid referenceId, string description, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be positive", nameof(amount));

        var dbContext = (DbContext)_context;
        // C2 Fix: Use RepeatableRead to prevent idempotency check TOCTOU at ReadCommitted
        await using var tx = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        try
        {
            var alreadyProcessed = await _context.LedgerEntries
                .AnyAsync(e => e.ReferenceId == referenceId.ToString(), ct);
            if (alreadyProcessed)
            {
                _logger.LogInformation("Ledger credit for reference {ReferenceId} already processed — skipping", referenceId);
                await tx.RollbackAsync(ct);
                return;
            }
            var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == sellerId, ct);
            var commissionRate = profile?.CommissionPercentage ?? 10.00m;
            var commission = Math.Round(amount * commissionRate / 100m, 2, MidpointRounding.AwayFromZero);
            var sellerAmount = amount - commission;

            var transactionId = Guid.NewGuid();
            // C1 Fix: FOR UPDATE locks prevent concurrent balance corruption
            var sellerAccount = await GetOrCreateAccountWithLock(sellerId, AccountType.SellerPending, currency, ct);
            var platformHoldingAccount = await GetOrCreateAccountWithLock(SystemPlatformId, AccountType.PlatformHolding, currency, ct);
            var platformRevenueAccount = await GetOrCreateAccountWithLock(SystemPlatformId, AccountType.PlatformRevenue, currency, ct);

            // Double-entry bookkeeping:
            // All three balances increase when a payment arrives.
            // UpdateBalance uses Credit=add, Debit=subtract for running balances.
            // LedgerEntry.Type records the accounting classification:
            //   Debit PlatformHolding (asset increase) = $amount
            //   Credit SellerPending (liability increase) = $sellerAmount
            //   Credit PlatformRevenue (revenue increase) = $commission
            // Invariant: Total Debits == Total Credits ($amount == $sellerAmount + $commission)
            platformHoldingAccount.UpdateBalance(amount, EntryType.Credit);
            sellerAccount.UpdateBalance(sellerAmount, EntryType.Credit);
            platformRevenueAccount.UpdateBalance(commission, EntryType.Credit);

            var refId = referenceId.ToString();
            _context.LedgerEntries.AddRange(
                LedgerEntry.Create(platformHoldingAccount.Id, transactionId, amount, EntryType.Debit, description, refId),
                LedgerEntry.Create(sellerAccount.Id, transactionId, sellerAmount, EntryType.Credit, description, refId),
                LedgerEntry.Create(platformRevenueAccount.Id, transactionId, commission, EntryType.Credit, $"Commission: {description}", refId));

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("Ledger credit for seller {SellerId}: amount={Amount}, commission={Commission}, seller={SellerAmount}, ref={ReferenceId}",
                sellerId, amount, commission, sellerAmount, referenceId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Reverses a seller credit (e.g., on refund). Idempotent by referenceId.
    /// </summary>
    public async Task DebitSellerAsync(Guid sellerId, decimal amount, string currency, Guid referenceId, string description, CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("Amount must be positive", nameof(amount));

        var refId = $"REFUND:{referenceId}";

        var dbContext = (DbContext)_context;
        await using var tx = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        try
        {
            var alreadyProcessed = await _context.LedgerEntries.AnyAsync(e => e.ReferenceId == refId, ct);
            if (alreadyProcessed)
            {
                _logger.LogInformation("Ledger debit for reference {ReferenceId} already processed — skipping", referenceId);
                await tx.RollbackAsync(ct);
                return;
            }

            var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == sellerId, ct);
            var commissionRate = profile?.CommissionPercentage ?? 10.00m;
            var commission = Math.Round(amount * commissionRate / 100m, 2, MidpointRounding.AwayFromZero);
            var sellerAmount = amount - commission;

            var transactionId = Guid.NewGuid();

            // Deterministic: find the account that was originally credited for this reference
            var creditedAccountId = await _context.LedgerEntries
                .Where(e => e.ReferenceId == referenceId.ToString())
                .Join(_context.LedgerAccounts, e => e.AccountId, a => a.Id, (e, a) => new { Entry = e, Account = a })
                .Where(x => x.Account.OwnerId == sellerId &&
                             (x.Account.Type == AccountType.SellerPending || x.Account.Type == AccountType.SellerPayable))
                .Select(x => x.Account.Id)
                .FirstOrDefaultAsync(ct);

            // H6 Fix: FOR UPDATE on all accounts to prevent concurrent balance corruption
            var sellerAccount = creditedAccountId != Guid.Empty
                ? await LockAccountById(creditedAccountId, ct)
                : null;

            var platformHoldingAccount = await LockAccount(SystemPlatformId, AccountType.PlatformHolding, currency, ct);
            var platformRevenueAccount = await LockAccount(SystemPlatformId, AccountType.PlatformRevenue, currency, ct);

            if (sellerAccount == null || platformHoldingAccount == null || platformRevenueAccount == null)
            {
                _logger.LogWarning("Cannot debit seller {SellerId} — accounts not found", sellerId);
                await tx.RollbackAsync(ct);
                return;
            }

            // Reverse the credit (balanced double-entry):
            // All three balances decrease on refund.
            // UpdateBalance uses Debit=subtract for running balances.
            // LedgerEntry.Type records the accounting classification:
            //   Debit SellerPending/Payable (liability decreases) = $sellerAmount
            //   Debit PlatformRevenue (revenue decreases) = $commission
            //   Credit PlatformHolding (asset decreases) = $amount
            // Invariant: Total Debits == Total Credits ($sellerAmount + $commission == $amount)
            sellerAccount.UpdateBalance(sellerAmount, EntryType.Debit);
            platformRevenueAccount.UpdateBalance(commission, EntryType.Debit);
            platformHoldingAccount.UpdateBalance(amount, EntryType.Debit);

            _context.LedgerEntries.AddRange(
                LedgerEntry.Create(sellerAccount.Id, transactionId, sellerAmount, EntryType.Debit, description, refId),
                LedgerEntry.Create(platformRevenueAccount.Id, transactionId, commission, EntryType.Debit, $"Commission reversal: {description}", refId),
                LedgerEntry.Create(platformHoldingAccount.Id, transactionId, amount, EntryType.Credit, description, refId));

            await _context.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("Ledger debit for seller {SellerId}: amount={Amount}, ref={ReferenceId}", sellerId, amount, referenceId);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public Task<bool> HasCreditForReferenceAsync(Guid referenceId, CancellationToken ct = default)
    {
        return _context.LedgerEntries.AnyAsync(e => e.ReferenceId == referenceId.ToString(), ct);
    }

    public async Task<decimal> GetBalanceAsync(Guid sellerId, AccountType type, string currency, CancellationToken ct = default)
    {
        var account = await _context.LedgerAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OwnerId == sellerId && a.Type == type && a.Currency == currency, ct);
        return account?.Balance ?? 0;
    }

    /// <summary>
    /// C1 Fix: Loads account with FOR UPDATE lock to prevent concurrent balance corruption.
    /// Creates the account if it doesn't exist (first credit for this owner/type/currency).
    /// </summary>
    private async Task<LedgerAccount> GetOrCreateAccountWithLock(Guid ownerId, AccountType type, string currency, CancellationToken ct)
    {
        var dbContext = (DbContext)_context;
        var typeInt = (int)type;

        var account = await _context.LedgerAccounts
            .FromSqlRaw(
                """
                SELECT *, xmin FROM payouts."LedgerAccounts"
                WHERE "OwnerId" = {0} AND "Type" = {1} AND "Currency" = {2}
                FOR UPDATE
                """,
                ownerId, typeInt, currency)
            .FirstOrDefaultAsync(ct);

        if (account == null)
        {
            account = LedgerAccount.Create(ownerId, type, currency);
            _context.LedgerAccounts.Add(account);
            await _context.SaveChangesAsync(ct);

            // Re-lock the newly created row
            account = await _context.LedgerAccounts
                .FromSqlRaw(
                    """
                    SELECT *, xmin FROM payouts."LedgerAccounts"
                    WHERE "OwnerId" = {0} AND "Type" = {1} AND "Currency" = {2}
                    FOR UPDATE
                    """,
                    ownerId, typeInt, currency)
                .FirstAsync(ct);
        }

        return account;
    }

    private async Task<LedgerAccount?> LockAccount(Guid ownerId, AccountType type, string currency, CancellationToken ct)
    {
        var typeInt = (int)type;
        return await _context.LedgerAccounts
            .FromSqlRaw(
                """
                SELECT *, xmin FROM payouts."LedgerAccounts"
                WHERE "OwnerId" = {0} AND "Type" = {1} AND "Currency" = {2}
                FOR UPDATE
                """,
                ownerId, typeInt, currency)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<LedgerAccount?> LockAccountById(Guid accountId, CancellationToken ct)
    {
        return await _context.LedgerAccounts
            .FromSqlRaw(
                """
                SELECT *, xmin FROM payouts."LedgerAccounts"
                WHERE "Id" = {0}
                FOR UPDATE
                """,
                accountId)
            .FirstOrDefaultAsync(ct);
    }
}
