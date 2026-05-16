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

        // Idempotency: if entries already exist for this reference, skip
        var alreadyProcessed = await _context.LedgerEntries
            .AnyAsync(e => e.ReferenceId == referenceId.ToString(), ct);
        if (alreadyProcessed)
        {
            _logger.LogInformation("Ledger credit for reference {ReferenceId} already processed — skipping", referenceId);
            return;
        }

        var dbContext = (DbContext)_context;
        await using var tx = await dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == sellerId, ct);
            var commissionRate = profile?.CommissionPercentage ?? 10.00m;
            var commission = Math.Round(amount * commissionRate / 100m, 2, MidpointRounding.AwayFromZero);
            var sellerAmount = amount - commission;

            var transactionId = Guid.NewGuid();
            var sellerAccount = await GetOrCreateAccountInTx(sellerId, AccountType.SellerPending, currency, ct);
            var platformHoldingAccount = await GetOrCreateAccountInTx(SystemPlatformId, AccountType.PlatformHolding, currency, ct);
            var platformRevenueAccount = await GetOrCreateAccountInTx(SystemPlatformId, AccountType.PlatformRevenue, currency, ct);

            // Correct double-entry bookkeeping (cash-flow semantics):
            // 1. Platform receives money → Credit PlatformHolding (balance increases)
            // 2. Platform takes commission → Credit PlatformRevenue
            // 3. Seller's share → Credit SellerPending
            // Net: PlatformHolding += amount, SellerPending += sellerAmount, PlatformRevenue += commission
            // The holding account retains the full amount; seller+commission entries are the breakdown.
            platformHoldingAccount.UpdateBalance(amount, EntryType.Credit);
            sellerAccount.UpdateBalance(sellerAmount, EntryType.Credit);
            platformRevenueAccount.UpdateBalance(commission, EntryType.Credit);

            var refId = referenceId.ToString();
            _context.LedgerEntries.AddRange(
                LedgerEntry.Create(platformHoldingAccount.Id, transactionId, amount, EntryType.Credit, description, refId),
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
        var alreadyProcessed = await _context.LedgerEntries.AnyAsync(e => e.ReferenceId == refId, ct);
        if (alreadyProcessed)
        {
            _logger.LogInformation("Ledger debit for reference {ReferenceId} already processed — skipping", referenceId);
            return;
        }

        var dbContext = (DbContext)_context;
        await using var tx = await dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == sellerId, ct);
            var commissionRate = profile?.CommissionPercentage ?? 10.00m;
            var commission = Math.Round(amount * commissionRate / 100m, 2, MidpointRounding.AwayFromZero);
            var sellerAmount = amount - commission;

            var transactionId = Guid.NewGuid();

            // Try pending first, then payable
            var sellerAccount = await _context.LedgerAccounts
                .FirstOrDefaultAsync(a => a.OwnerId == sellerId && a.Type == AccountType.SellerPending && a.Currency == currency, ct)
                ?? await _context.LedgerAccounts
                .FirstOrDefaultAsync(a => a.OwnerId == sellerId && a.Type == AccountType.SellerPayable && a.Currency == currency, ct);

            var platformHoldingAccount = await _context.LedgerAccounts
                .FirstOrDefaultAsync(a => a.OwnerId == SystemPlatformId && a.Type == AccountType.PlatformHolding && a.Currency == currency, ct);
            var platformRevenueAccount = await _context.LedgerAccounts
                .FirstOrDefaultAsync(a => a.OwnerId == SystemPlatformId && a.Type == AccountType.PlatformRevenue && a.Currency == currency, ct);

            if (sellerAccount == null || platformHoldingAccount == null || platformRevenueAccount == null)
            {
                _logger.LogWarning("Cannot debit seller {SellerId} — accounts not found", sellerId);
                await tx.RollbackAsync(ct);
                return;
            }

            // Reverse the credit: debit seller, debit platform revenue, debit holding
            sellerAccount.UpdateBalance(sellerAmount, EntryType.Debit);
            platformRevenueAccount.UpdateBalance(commission, EntryType.Debit);
            platformHoldingAccount.UpdateBalance(amount, EntryType.Debit);

            _context.LedgerEntries.AddRange(
                LedgerEntry.Create(sellerAccount.Id, transactionId, sellerAmount, EntryType.Debit, description, refId),
                LedgerEntry.Create(platformRevenueAccount.Id, transactionId, commission, EntryType.Debit, $"Commission reversal: {description}", refId),
                LedgerEntry.Create(platformHoldingAccount.Id, transactionId, amount, EntryType.Debit, description, refId));

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

    public async Task<bool> HasCreditForReferenceAsync(Guid referenceId, CancellationToken ct = default)
    {
        return await _context.LedgerEntries.AnyAsync(e => e.ReferenceId == referenceId.ToString(), ct);
    }

    public async Task<decimal> GetBalanceAsync(Guid sellerId, AccountType type, string currency, CancellationToken ct = default)
    {
        var account = await _context.LedgerAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.OwnerId == sellerId && a.Type == type && a.Currency == currency, ct);
        return account?.Balance ?? 0;
    }

    private async Task<LedgerAccount> GetOrCreateAccountInTx(Guid ownerId, AccountType type, string currency, CancellationToken ct)
    {
        var account = await _context.LedgerAccounts
            .FirstOrDefaultAsync(a => a.OwnerId == ownerId && a.Type == type && a.Currency == currency, ct);

        if (account == null)
        {
            account = LedgerAccount.Create(ownerId, type, currency);
            _context.LedgerAccounts.Add(account);
            // No SaveChangesAsync here — caller's transaction commits all at once
        }

        return account;
    }
}
