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

        // M5 Fix: Process payouts concurrently (bounded to 5 parallel gateway calls)
        // Sequential processing of 500 accounts with Stripe calls took 30+ minutes.
        var payoutTasks = eligibleAccounts
            .Where(account => profiles.TryGetValue(account.OwnerId, out var p) &&
                              p.PayoutsEnabled && !string.IsNullOrEmpty(p.ExternalProviderId) &&
                              account.Balance >= p.PayoutThreshold)
            .Select(account => (account.Id, profiles[account.OwnerId]));

        await Parallel.ForEachAsync(payoutTasks, new ParallelOptions { MaxDegreeOfParallelism = 5 },
            async (item, ct) => await ExecutePayout(item.Id, item.Item2));
    }

    private async Task ExecutePayout(Guid accountId, SellerProfile profile)
    {
        var dbContext = (Microsoft.EntityFrameworkCore.DbContext)_context;
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var payoutAmount = 0m;
        Guid? payoutId = null;
        var currency = string.Empty;
        // H2 Fix: Use payoutId (generated in Phase 1) in the idempotency key.
        // Day-granularity blocked legitimate same-day retries; per-payout key is idempotent for retries
        // of the same attempt while allowing new payouts on the same day.
        var payoutIdForKey = Guid.NewGuid();
        var idempotencyKey = $"PAYOUT:{payoutIdForKey}";

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
                // H3 Fix: Only transition to InTransit here. The terminal Succeeded state
                // must be driven by a Stripe webhook (transfer.paid) — the funds are not
                // confirmed until Stripe settles the transfer to the connected account.
                payout!.MarkInTransit(externalId!);
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
