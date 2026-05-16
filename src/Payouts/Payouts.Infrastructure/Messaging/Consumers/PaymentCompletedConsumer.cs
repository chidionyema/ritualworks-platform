using Haworks.Contracts.Payments;
using Haworks.Payouts.Application.Ledger.Services;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Payouts.Infrastructure.Messaging.Consumers;

public class PaymentCompletedConsumer : IConsumer<PaymentCompletedEvent>
{
    private readonly ILedgerService _ledgerService;
    private readonly ILogger<PaymentCompletedConsumer> _logger;

    public PaymentCompletedConsumer(ILedgerService ledgerService, ILogger<PaymentCompletedConsumer> logger)
    {
        _ledgerService = ledgerService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PaymentCompletedEvent> context)
    {
        var evt = context.Message;

        if (evt.SellerId == Guid.Empty)
        {
            _logger.LogWarning("PaymentCompletedEvent for Order {OrderId} has no SellerId — cannot credit seller", evt.OrderId);
            return;
        }

        // Idempotency: check if this payment was AlreadyProcessed via the ReferenceId (PaymentId)
        var alreadyCredited = await _ledgerService.HasCreditForReferenceAsync(evt.PaymentId, context.CancellationToken);
        if (alreadyCredited)
        {
            _logger.LogInformation("Payment {PaymentId} already credited — skipping duplicate", evt.PaymentId);
            return;
        }

        _logger.LogInformation("Processing payment completion for Order: {OrderId}, Seller: {SellerId}, Amount: {Amount}",
            evt.OrderId, evt.SellerId, evt.Amount);

        await _ledgerService.CreditSellerAsync(
            evt.SellerId,
            evt.Amount,
            evt.Currency,
            evt.PaymentId,
            $"Payment for Order {evt.OrderId}",
            context.CancellationToken);
    }
}
