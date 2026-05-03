namespace Haworks.Contracts.Payments;

/// <summary>
/// Published when a checkout session expires without payment completion.
/// This triggers order expiration and stock release in the Orders context.
///
/// Consumers should:
/// - Mark the associated order as expired
/// - Trigger stock release for reserved items
/// - Record the expiration for audit purposes
/// </summary>
public sealed record CheckoutSessionExpiredEvent : DomainEvent
{
    /// <summary>The payment record ID.</summary>
    public required Guid PaymentId { get; init; }

    /// <summary>The order this payment is for.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga ID for correlation.</summary>
    public Guid? SagaId { get; init; }

    /// <summary>The provider's session ID.</summary>
    public required string SessionId { get; init; }

    /// <summary>The payment provider.</summary>
    public required string Provider { get; init; }
}
