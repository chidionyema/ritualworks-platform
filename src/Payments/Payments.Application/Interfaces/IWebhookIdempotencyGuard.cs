using Haworks.Payments.Domain;

namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Guards against duplicate webhook processing.
/// </summary>
public interface IWebhookIdempotencyGuard
{
    /// <summary>
    /// Checks if a webhook event has already been processed.
    /// </summary>
    Task<bool> IsAlreadyProcessedAsync(
        PaymentProvider provider,
        string eventId,
        CancellationToken ct = default);

    /// <summary>
    /// Marks a webhook event as processed.
    /// </summary>
    Task MarkProcessedAsync(
        PaymentProvider provider,
        string eventId,
        string eventType,
        CancellationToken ct = default);
}
