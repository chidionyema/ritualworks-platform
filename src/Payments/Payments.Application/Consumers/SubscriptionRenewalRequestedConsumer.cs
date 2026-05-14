using MassTransit;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Consumers;

public sealed class SubscriptionRenewalRequestedConsumer(
    ILogger<SubscriptionRenewalRequestedConsumer> logger) 
    : IConsumer<SubscriptionRenewalRequestedEvent>
{
    public async Task Consume(ConsumeContext<SubscriptionRenewalRequestedEvent> context)
    {
        var msg = context.Message;
        logger.LogInformation("Processing renewal request for subscription {SubscriptionId}", msg.ProviderSubscriptionId);

        // In a real implementation, we would call the provider to charge the customer.
        // For this demo/saga orchestration, we'll let the test or the provider webhook 
        // drive the success/failure events. 
        
        // However, to make the system "alive" for demos, we could emit a success
        // unless a specific flag is set. For now, we'll keep it passive so the saga
        // state transitions are controlled by external events (webhooks/tests).
        
        await Task.CompletedTask;
    }
}
