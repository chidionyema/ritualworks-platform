using Haworks.BuildingBlocks.Telemetry;
using Haworks.Contracts.Payments;
using Haworks.Payments.Application.Interfaces;
using Haworks.Payments.Domain;
using Haworks.Payments.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Haworks.Payments.Application.Webhooks;

/// <summary>
/// Handles payment amount mismatches in a consistent way across providers.
/// Flags the payment for review and publishes an event for cross-context updates.
/// </summary>
public sealed class PaymentAmountMismatchHandler : IPaymentAmountMismatchHandler
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IDomainEventPublisher _eventPublisher;
    private readonly ITelemetryService _telemetry;
    private readonly ILogger<PaymentAmountMismatchHandler> _logger;

    public PaymentAmountMismatchHandler(
        IPaymentRepository paymentRepository,
        IDomainEventPublisher eventPublisher,
        ITelemetryService telemetry,
        ILogger<PaymentAmountMismatchHandler> logger)
    {
        _paymentRepository = paymentRepository ?? throw new ArgumentNullException(nameof(paymentRepository));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task HandleMismatchAsync(
        Payment payment,
        decimal actualPaid,
        decimal expectedTotal,
        PaymentProvider provider,
        CancellationToken ct = default)
    {
        var difference = Math.Abs(actualPaid - expectedTotal);
        var reason = $"{provider} paid {actualPaid:C}, Expected {expectedTotal:C}, Diff {difference:C}";

        _logger.LogCritical(
            "AMOUNT MISMATCH for Payment {PaymentId}: {Reason}",
            payment.Id,
            reason);

        // Flag payment in the Payments context — outbox handles this:
        // SaveChangesAsync + PublishAsync are atomic via the MassTransit outbox.
        payment.Flag();

        // Publish event for Orders context to handle order status update.
        await _eventPublisher.PublishAsync(new PaymentAmountMismatchEvent
        {
            PaymentId = payment.Id,
            OrderId = payment.OrderId,
            Provider = provider.ToString(),
            ActualPaid = actualPaid,
            ExpectedTotal = expectedTotal,
            Difference = difference,
            Reason = reason
        }, ct);

        await _paymentRepository.SaveChangesAsync(ct);

        _telemetry.TrackEvent("PaymentAmountMismatch", new Dictionary<string, string>
        {
            ["Provider"] = provider.ToString(),
            ["OrderId"] = payment.OrderId.ToString(),
            ["PaymentId"] = payment.Id.ToString(),
            ["ActualPaid"] = actualPaid.ToString("F2"),
            ["ExpectedTotal"] = expectedTotal.ToString("F2"),
            ["Difference"] = difference.ToString("F2")
        });
    }
}
