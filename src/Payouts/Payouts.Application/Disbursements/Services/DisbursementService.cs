using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Domain.Aggregates;
using Haworks.Payouts.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Payouts.Application.Disbursements.Services;

public interface IDisbursementService
{
    Task ProcessEligiblePayoutsAsync();
}

public class DisbursementService : IDisbursementService
{
    private readonly IPayoutsDbContext _context;
    private readonly IPayoutGateway _payoutGateway;
    private readonly ILogger<DisbursementService> _logger;

    public DisbursementService(IPayoutsDbContext context, IPayoutGateway payoutGateway, ILogger<DisbursementService> logger)
    {
        _context = context;
        _payoutGateway = payoutGateway;
        _logger = logger;
    }

    public async Task ProcessEligiblePayoutsAsync()
    {
        var eligibleAccounts = await _context.LedgerAccounts
            .AsNoTracking()
            .Where(a => a.Type == AccountType.SellerPayable && a.Balance > 0)
            .Take(500)
            .ToListAsync();

        var ownerIds = eligibleAccounts.Select(a => a.OwnerId).ToList();
        var profiles = await _context.SellerProfiles
            .Where(p => ownerIds.Contains(p.SellerId))
            .ToDictionaryAsync(p => p.SellerId);

        foreach (var account in eligibleAccounts)
        {
            if (!profiles.TryGetValue(account.OwnerId, out var profile)) continue;
            if (!profile.PayoutsEnabled || string.IsNullOrEmpty(profile.ExternalProviderId)) continue;
            if (account.Balance < profile.PayoutThreshold) continue;

            await ExecutePayout(account.Id, profile);
        }
    }

    private async Task ExecutePayout(Guid accountId, SellerProfile profile)
    {
        var dbContext = (Microsoft.EntityFrameworkCore.DbContext)_context;
        await using var tx = await dbContext.Database.BeginTransactionAsync();
        try
        {
            // FOR UPDATE lock prevents concurrent payouts against the same account
            var account = await _context.LedgerAccounts
                .FromSqlRaw("SELECT *, xmin FROM payouts.\"LedgerAccounts\" WHERE \"Id\" = {0} FOR UPDATE", accountId)
                .FirstAsync();

            var payoutAmount = account.Balance;

            if (payoutAmount <= 0 || payoutAmount < profile.PayoutThreshold)
            {
                await tx.RollbackAsync();
                return;
            }

            var payout = Payout.Create(profile.SellerId, payoutAmount, account.Currency);
            _context.Payouts.Add(payout);
            await _context.SaveChangesAsync(CancellationToken.None);

            try
            {
                var (externalId, status) = await _payoutGateway.InitiatePayoutAsync(
                    profile.ExternalProviderId!, payoutAmount, account.Currency, $"Payout for {profile.SellerId}");

                if (status == PayoutStatus.Succeeded)
                {
                    payout.MarkInTransit(externalId);
                    payout.MarkSucceeded();
                    account.UpdateBalance(payoutAmount, EntryType.Debit);
                    var entry = LedgerEntry.Create(account.Id, Guid.NewGuid(), payoutAmount, EntryType.Debit, "Payout processed", payout.Id.ToString());
                    _context.LedgerEntries.Add(entry);
                }
                else
                {
                    payout.MarkFailed("Gateway returned non-success status");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process payout for seller {SellerId}", profile.SellerId);
                payout.MarkFailed(ex.Message);
            }

            await _context.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
