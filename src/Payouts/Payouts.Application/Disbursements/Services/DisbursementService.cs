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

            // Re-load tracked for mutation
            var trackedAccount = await _context.LedgerAccounts.FirstAsync(a => a.Id == account.Id);
            await ExecutePayout(trackedAccount, profile);
        }
    }

    private async Task ExecutePayout(LedgerAccount account, SellerProfile profile)
    {
        var payoutAmount = account.Balance;
        var payout = Payout.Create(profile.SellerId, payoutAmount, account.Currency);
        _context.Payouts.Add(payout);
        await _context.SaveChangesAsync();

        try
        {
            var (externalId, status) = await _payoutGateway.InitiatePayoutAsync(profile.ExternalProviderId!, payoutAmount, account.Currency, $"Payout for {profile.SellerId}");
            if (status == PayoutStatus.Succeeded)
            {
                payout.MarkInTransit(externalId);
                payout.MarkSucceeded();
                var transactionId = Guid.NewGuid();
                account.UpdateBalance(payoutAmount, EntryType.Debit);
                var entry = LedgerEntry.Create(account.Id, transactionId, payoutAmount, EntryType.Debit, "Payout processed", payout.Id.ToString());
                _context.LedgerEntries.Add(entry);
            }
            else payout.MarkFailed("Gateway returned non-success status");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process payout for seller {SellerId}", profile.SellerId);
            payout.MarkFailed(ex.Message);
        }
        await _context.SaveChangesAsync();
    }
}
