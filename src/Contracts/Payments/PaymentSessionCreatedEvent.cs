namespace Haworks.Contracts.Payments;

/// <summary>
/// Published when a payment session is created with the payment provider.
/// Consumers can use this to:
/// - Track checkout funnel metrics
/// - Monitor payment provider response times
/// - Trigger session expiry tracking
/// </summary>
public sealed record PaymentSessionCreatedEvent : DomainEvent
{
    /// <summary>The order this payment session is for.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga correlation ID for distributed tracing.</summary>
    public required Guid SagaId { get; init; }

    /// <summary>The user who initiated the checkout (for SignalR group targeting).</summary>
    public required string UserId { get; init; }

    /// <summary>The payment record ID.</summary>
    public required Guid PaymentId { get; init; }

    /// <summary>The provider's session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>The checkout URL for the customer.</summary>
    public required string CheckoutUrl { get; init; }

    /// <summary>The payment provider (e.g., "Stripe", "PayPal").</summary>
    public required string Provider { get; init; }

    /// <summary>The total amount for this payment session.</summary>
    public required decimal Amount { get; init; }

    /// <summary>The currency code (e.g., "USD").</summary>
    public required string Currency { get; init; }
}
