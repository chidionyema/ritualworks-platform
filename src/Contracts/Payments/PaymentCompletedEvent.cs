namespace Haworks.Contracts.Payments;

/// <summary>
/// Published when a payment is successfully completed.
/// Consumers can use this to:
/// - Update order status to paid
/// - Send payment confirmation emails
/// - Trigger shipping/fulfillment
/// - Update financial records
/// </summary>
public sealed record PaymentCompletedEvent : DomainEvent
{
    /// <summary>The unique identifier of the payment.</summary>
    public required Guid PaymentId { get; init; }

    /// <summary>The order this payment is for.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga correlation ID for distributed tracing.</summary>
    public required Guid SagaId { get; init; }

    /// <summary>The amount paid in cents.</summary>
    public required long AmountCents { get; init; }

    /// <summary>The currency code (e.g., "USD", "EUR").</summary>
    public required string Currency { get; init; }

    /// <summary>The payment provider used (e.g., "Stripe", "PayPal").</summary>
    public required string Provider { get; init; }

    /// <summary>The provider's transaction reference.</summary>
    public string? TransactionReference { get; init; }

    /// <summary>The seller who should be credited for this payment (used by Payouts).</summary>
    public Guid SellerId { get; init; }
}
