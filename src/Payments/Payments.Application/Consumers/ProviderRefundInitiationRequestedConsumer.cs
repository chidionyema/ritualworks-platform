using MassTransit;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Consumers;

public sealed class ProviderRefundInitiationRequestedConsumer(
    IPaymentGateway gateway,
    IPaymentRepository paymentRepository,
    ILogger<ProviderRefundInitiationRequestedConsumer> logger) 
    : IConsumer<ProviderRefundInitiationRequestedEvent>
{
    public async Task Consume(ConsumeContext<ProviderRefundInitiationRequestedEvent> context)
    {
        var msg = context.Message;

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RefundId"] = msg.RefundId,
            ["PaymentId"] = msg.PaymentId,
            ["Provider"] = msg.Provider
        });

        var payment = await paymentRepository.GetByIdAsync(msg.PaymentId, context.CancellationToken);
        if (payment == null)
        {
            logger.LogError("Payment {PaymentId} not found for refund {RefundId}", msg.PaymentId, msg.RefundId);
            await context.Publish(new ProviderRefundFailedEvent
            {
                RefundId = msg.RefundId,
                ErrorCode = "PaymentNotFound",
                ErrorMessage = "Payment record not found"
            });
            return;
        }

        var result = await gateway.Refunds.CreateRefundAsync(new RefundRequest
        {
            TransactionId = payment.ProviderTransactionId ?? string.Empty,
            AmountCents = (long)Math.Round(msg.Amount * 100m, 0, MidpointRounding.AwayFromZero),
            Reason = "Refund requested via saga",
            Metadata = new Dictionary<string, string>
            {
                ["refund_id"] = msg.RefundId.ToString(),
                ["saga_id"] = msg.RefundId.ToString()
            }
        }, context.CancellationToken);

        if (result.Status == RefundStatus.Succeeded || result.Status == RefundStatus.Pending)
        {
            await context.Publish(new ProviderRefundInitiatedEvent
            {
                RefundId = msg.RefundId,
                ProviderRefundId = result.RefundId
            });
        }
        else
        {
            await context.Publish(new ProviderRefundFailedEvent
            {
                RefundId = msg.RefundId,
                ErrorCode = "ProviderError",
                ErrorMessage = result.FailureReason ?? "Unknown provider error"
            });
        }
    }
}
