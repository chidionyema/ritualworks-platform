using Haworks.Contracts.Payments;
using Haworks.Payouts.Application.Common.Interfaces;
using Haworks.Payouts.Application.Ledger.Services;
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

        // Find the seller by looking up the original credit entry for this payment
        var originalEntry = await _context.LedgerEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ReferenceId == evt.PaymentId.ToString(), context.CancellationToken);

        if (originalEntry == null)
        {
            _logger.LogWarning("No ledger entry found for PaymentId {PaymentId} — cannot reverse credit", evt.PaymentId);
            return;
        }

        // Find the seller account from the entry
        var sellerAccount = await _context.LedgerAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == originalEntry.AccountId, context.CancellationToken);

        if (sellerAccount == null)
        {
            _logger.LogWarning("No seller account found for entry {EntryId}", originalEntry.Id);
            return;
        }

        await _ledgerService.DebitSellerAsync(
            sellerAccount.OwnerId,
            refundAmount,
            evt.Currency,
            evt.PaymentId,
            $"Refund for Order {evt.OrderId}",
            context.CancellationToken);
    }
}
