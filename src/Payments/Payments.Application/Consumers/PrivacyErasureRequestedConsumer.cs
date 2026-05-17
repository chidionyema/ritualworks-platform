using Haworks.Contracts.Privacy;
using Haworks.Payments.Domain.Interfaces;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Consumers;

/// <summary>
/// Handles GDPR erasure for payments-svc: anonymises all payments and
/// subscriptions belonging to the requesting user then publishes
/// <see cref="PrivacyErasureCompleted"/> so the Privacy saga can track
/// completion across all bounded contexts.
/// </summary>
public sealed class PrivacyErasureRequestedConsumer(
    IPaymentRepository payments,
    ILogger<PrivacyErasureRequestedConsumer> logger
) : IConsumer<PrivacyErasureRequested>
{
    public async Task Consume(ConsumeContext<PrivacyErasureRequested> context)
    {
        var msg = context.Message;
        var userId = msg.UserId.ToString();

        logger.LogInformation(
            "GDPR erasure requested for UserId={UserId}, RequestId={RequestId}",
            msg.UserId, msg.RequestId);

        var totalAnonymised = 0;

        // Anonymise payments — query by userId via repository
        var paymentsByUser = await payments.ListByUserAsync(userId, context.CancellationToken);
        foreach (var payment in paymentsByUser)
        {
            payment.AnonymiseForPrivacy();
            totalAnonymised++;
        }

        // Anonymise subscription if present
        var subscription = await payments.GetSubscriptionByUserIdAsync(userId, context.CancellationToken);
        subscription?.AnonymiseForPrivacy();

        // MassTransit EF Outbox commits automatically

        logger.LogInformation("Anonymised {Count} payments for UserId={UserId}", totalAnonymised, msg.UserId);

        await context.Publish(new PrivacyErasureCompleted
        {
            RequestId = msg.RequestId,
            UserId = msg.UserId,
            ServiceName = "payments-svc"
        });
    }
}
