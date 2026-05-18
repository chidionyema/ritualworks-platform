namespace Haworks.Contracts.Payments;

/// <summary>
/// Published when a payment amount doesn't match the expected order total.
/// This indicates potential fraud, currency conversion issues, or processing errors.
///
/// Consumers should:
/// - Update the order status to RequiresReview
/// - Alert administrators for manual intervention
/// - Record the incident for audit purposes
/// </summary>
public sealed record PaymentAmountMismatchEvent : DomainEvent
{
    /// <summary>The payment record ID.</summary>
    public required Guid PaymentId { get; init; }

    /// <summary>The order this payment is for.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The payment provider (e.g., "Stripe", "PayPal").</summary>
    public required string Provider { get; init; }

    /// <summary>The actual amount paid.</summary>
    public required long ActualPaidCents { get; init; }

    /// <summary>The expected order total.</summary>
    public required long ExpectedTotalCents { get; init; }

    /// <summary>The absolute difference between actual and expected.</summary>
    public required long DifferenceCents { get; init; }

    /// <summary>Human-readable reason for the mismatch.</summary>
    public required string Reason { get; init; }
}
