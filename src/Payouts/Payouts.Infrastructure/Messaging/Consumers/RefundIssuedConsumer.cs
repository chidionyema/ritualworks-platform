using Haworks.Contracts.Payments;
using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Application.Ledger.Services;
using Haworks.Payouts.Domain.Enums;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Haworks.Payouts.Infrastructure.Messaging.Consumers;

/// <summary>
/// Reverses seller credit when a payment is refunded.
/// Uses the PaymentId to find the original seller via ledger entries,
/// then debits the equivalent amount from their pending/payable account.
/// </summary>
public class RefundIssuedConsumer : IConsumer<RefundIssuedEvent>
{
    private readonly ILedgerService _ledgerService;
    private readonly IPayoutsDbContext _context;
    private readonly ILogger<RefundIssuedConsumer> _logger;

    public RefundIssuedConsumer(ILedgerService ledgerService, IPayoutsDbContext context, ILogger<RefundIssuedConsumer> logger)
    {
        _ledgerService = ledgerService;
        _context = context;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<RefundIssuedEvent> context)
    {
        var evt = context.Message;
        var refundAmount = evt.AmountCents / 100m;

        _logger.LogInformation("Processing refund for Payment {PaymentId}, Order {OrderId}, Amount: {Amount}",
            evt.PaymentId, evt.OrderId, refundAmount);

        // Deterministic lookup: filter to seller accounts only (SellerPending or SellerPayable)
        var sellerEntry = await _context.LedgerEntries
            .AsNoTracking()
            .Where(e => e.ReferenceId == evt.PaymentId.ToString())
            .Join(_context.LedgerAccounts, e => e.AccountId, a => a.Id, (e, a) => new { Entry = e, Account = a })
            .Where(x => x.Account.Type == AccountType.SellerPending || x.Account.Type == AccountType.SellerPayable)
            .Select(x => new { x.Account.OwnerId, x.Account.Type })
            .FirstOrDefaultAsync(context.CancellationToken);

        if (sellerEntry == null)
        {
            _logger.LogWarning("No seller ledger entry found for PaymentId {PaymentId} — cannot reverse credit", evt.PaymentId);
            return;
        }

        // DebitSellerAsync internally prefixes refId with "REFUND:" so the reference key
        // is distinct from the original credit entry's reference.
        await _ledgerService.DebitSellerAsync(
            sellerEntry.OwnerId,
            refundAmount,
            evt.Currency,
            evt.PaymentId,
            $"Refund for Order {evt.OrderId}",
            context.CancellationToken);
    }
}
