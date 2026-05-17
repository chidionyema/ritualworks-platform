namespace Haworks.Contracts.Payments;

/// <summary>
/// Published when a payment session needs to be created with the payment provider.
/// Typically triggered after stock is successfully reserved.
/// Consumers:
/// - PaymentSessionConsumer: Creates session with Stripe/PayPal
/// </summary>
public sealed record PaymentSessionRequestedEvent : DomainEvent
{
    /// <summary>The order requiring a payment session.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga identifier for correlation.</summary>
    public required Guid SagaId { get; init; }

    /// <summary>Total amount to charge.</summary>
    public required decimal Amount { get; init; }

    /// <summary>Currency code (e.g., "USD").</summary>
    public required string Currency { get; init; }

    /// <summary>Authenticated user identifier (subject claim from JWT).</summary>
    public required string UserId { get; init; }

    /// <summary>Customer email for the payment session.</summary>
    public required string CustomerEmail { get; init; }

    /// <summary>Tax amount included in the total (may be 0 when not available).</summary>
    public decimal Tax { get; init; } = 0m;

    /// <summary>Line items for the payment session.</summary>
    public required IReadOnlyList<PaymentLineItemData> LineItems { get; init; }

    /// <summary>URL to redirect on successful payment.</summary>
    public required string SuccessUrl { get; init; }

    /// <summary>URL to redirect on cancelled payment.</summary>
    public required string CancelUrl { get; init; }

    /// <summary>Additional metadata to attach to the session.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Idempotency key for the payment provider.</summary>
    public string? IdempotencyKey { get; init; }
}
