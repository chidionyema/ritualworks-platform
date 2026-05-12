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
            .Where(a => a.Type == AccountType.SellerPayable && a.Balance > 0)
            .ToListAsync();

        foreach (var account in eligibleAccounts)
        {
            var profile = await _context.SellerProfiles.FirstOrDefaultAsync(p => p.SellerId == account.OwnerId);
            if (profile == null || !profile.PayoutsEnabled || string.IsNullOrEmpty(profile.ExternalProviderId)) continue;
            if (account.Balance < profile.PayoutThreshold) continue;

            await ExecutePayout(account, profile);
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
