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
            .OrderBy(a => a.Id)
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
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var payoutAmount = 0m;
        Guid? payoutId = null;
        var currency = string.Empty;
        var idempotencyKey = $"PAYOUT:{accountId}:{DateTimeOffset.UtcNow:yyyy-MM-dd}";

        // =====================================================================
        // PHASE 1: ATOMIC LOCAL RESERVATION (debit balance, create Payout)
        // =====================================================================
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync();

            var account = await _context.LedgerAccounts
                .FromSqlRaw("SELECT *, xmin FROM payouts.\"LedgerAccounts\" WHERE \"Id\" = {0} FOR UPDATE", accountId)
                .FirstAsync();

            if (account.Balance <= 0 || account.Balance < profile.PayoutThreshold)
            {
                await tx.RollbackAsync();
                return;
            }

            payoutAmount = account.Balance;
            currency = account.Currency;

            var payout = Payout.Create(profile.SellerId, payoutAmount, currency);
            payoutId = payout.Id;
            _context.Payouts.Add(payout);

            account.UpdateBalance(payoutAmount, EntryType.Debit);
            var entry = LedgerEntry.Create(account.Id, Guid.NewGuid(), payoutAmount, EntryType.Debit, "Payout initiated", payout.Id.ToString());
            _context.LedgerEntries.Add(entry);

            await _context.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync();
        });

        if (payoutId == null) return;

        // =====================================================================
        // PHASE 2: EXTERNAL GATEWAY CALL (outside DB locks)
        // =====================================================================
        string? externalId = null;
        var isSuccess = false;
        string? errorMessage = null;

        try
        {
            var (gatewayExternalId, status) = await _payoutGateway.InitiatePayoutAsync(
                profile.ExternalProviderId!, payoutAmount, currency, $"Payout for {profile.SellerId}",
                idempotencyKey);

            isSuccess = status == PayoutStatus.Succeeded;
            externalId = gatewayExternalId;
            if (!isSuccess) errorMessage = "Gateway returned non-success status";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway error for seller {SellerId}", profile.SellerId);
            errorMessage = ex.Message;
        }

        // =====================================================================
        // PHASE 3: RESOLUTION (commit success or refund failure)
        // =====================================================================
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await dbContext.Database.BeginTransactionAsync();

            var payout = await _context.Payouts.FindAsync([payoutId!.Value], CancellationToken.None);

            if (isSuccess)
            {
                payout!.MarkInTransit(externalId!);
                payout.MarkSucceeded();
            }
            else
            {
                payout!.MarkFailed(errorMessage ?? "Unknown error");

                // REFUND: re-credit the account since gateway failed
                var account = await _context.LedgerAccounts
                    .FromSqlRaw("SELECT *, xmin FROM payouts.\"LedgerAccounts\" WHERE \"Id\" = {0} FOR UPDATE", accountId)
                    .FirstAsync();

                account.UpdateBalance(payoutAmount, EntryType.Credit);
                var entry = LedgerEntry.Create(account.Id, Guid.NewGuid(), payoutAmount, EntryType.Credit, "Payout failed — refund", payout.Id.ToString());
                _context.LedgerEntries.Add(entry);
            }

            await _context.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync();
        });
    }
}
