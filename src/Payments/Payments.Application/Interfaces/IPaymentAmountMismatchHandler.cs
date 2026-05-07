using Haworks.Payments.Domain;

namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Handles payment amount mismatches in a consistent way across providers.
/// </summary>
public interface IPaymentAmountMismatchHandler
{
    /// <summary>
    /// Handles a payment amount mismatch by flagging the payment and notifying other contexts.
    /// </summary>
    Task HandleMismatchAsync(
        Payment payment,
        decimal actualPaid,
        decimal expectedTotal,
        PaymentProvider provider,
        CancellationToken ct = default);
}
