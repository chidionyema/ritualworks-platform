namespace Haworks.Contracts.Payments;

/// <summary>
/// Published when a payment is verified with the payment provider.
/// This confirms the payment session was completed successfully.
/// Consumers can use this to:
/// - Record payment completion metrics
/// - Trigger order status updates
/// - Send payment confirmation notifications
/// </summary>
public sealed record PaymentVerifiedEvent : DomainEvent
{
    /// <summary>The payment record ID.</summary>
    public required Guid PaymentId { get; init; }

    /// <summary>The order this payment is for.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The payment provider (e.g., "Stripe", "PayPal").</summary>
    public required string Provider { get; init; }

    /// <summary>The provider's transaction/payment intent reference.</summary>
    public string? TransactionReference { get; init; }

    /// <summary>The verified payment amount.</summary>
    public required long AmountCents { get; init; }

    /// <summary>The currency code.</summary>
    public required string Currency { get; init; }
}
