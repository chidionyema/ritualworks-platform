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
        var @event = context.Message;
        _logger.LogInformation("Processing payment completion for Order: {OrderId}, Amount: {Amount}", @event.OrderId, @event.Amount);
        Guid sellerId = Guid.NewGuid(); // Placeholder
        await _ledgerService.CreditSellerAsync(sellerId, @event.Amount, @event.Currency, @event.PaymentId, $"Payment for Order {@event.OrderId}");
    }
}
