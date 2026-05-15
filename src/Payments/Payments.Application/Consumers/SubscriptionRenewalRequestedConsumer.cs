using MassTransit;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Consumers;

public sealed class SubscriptionRenewalRequestedConsumer(
    IPaymentGateway paymentGateway,
    ILogger<SubscriptionRenewalRequestedConsumer> logger)
    : IConsumer<SubscriptionRenewalRequestedEvent>
{
    public async Task Consume(ConsumeContext<SubscriptionRenewalRequestedEvent> context)
    {
        var msg = context.Message;
        logger.LogInformation("Processing renewal request for subscription {SubscriptionId}", msg.ProviderSubscriptionId);

        try
        {
            await paymentGateway.Subscriptions.HandleSubscriptionEventAsync(new SubscriptionEvent
            {
                SubscriptionId = msg.ProviderSubscriptionId,
                EventType = SubscriptionEventType.Renewed,
                NewStatus = SubscriptionStatus.Active,
                Provider = paymentGateway.ActiveProvider
            }, context.CancellationToken);

            logger.LogInformation("Subscription {SubscriptionId} renewed successfully", msg.ProviderSubscriptionId);

            await context.Publish(new SubscriptionRenewedEvent
            {
                SubscriptionId = msg.ProviderSubscriptionId,
                UserId = string.Empty,
                Provider = paymentGateway.ActiveProvider,
                AmountCents = 0,
                Currency = "USD",
                NewPeriodEnd = DateTime.UtcNow.AddMonths(1),
                RenewedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscription renewal failed for {SubscriptionId}", msg.ProviderSubscriptionId);

            await context.Publish(new SubscriptionRenewalFailedEvent
            {
                SubscriptionId = msg.SubscriptionId,
                ErrorCode = ex.GetType().Name,
                ErrorMessage = ex.Message
            });
        }
    }
}
