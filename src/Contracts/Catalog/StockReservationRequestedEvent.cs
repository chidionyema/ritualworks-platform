namespace Haworks.Contracts.Catalog;

/// <summary>
/// Published by checkout-orchestrator-svc when stock needs to be reserved
/// for a new checkout. catalog-svc consumes this and either reserves stock
/// (publishing <see cref="StockReservedEvent"/>) or fails (publishing
/// <see cref="StockReservationFailedEvent"/>).
///
/// Phase 5 closes the gap left by Phase 2c, which exposed stock reservation
/// only as a synchronous REST POST. The saga choreography requires an
/// event-driven request channel so the orchestrator doesn't make blocking
/// HTTP calls into catalog-svc.
/// </summary>
public sealed record StockReservationRequestedEvent : DomainEvent
{
    /// <summary>The order this reservation is for (correlation back to orders-svc).</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga correlation ID.</summary>
    public required Guid SagaId { get; init; }

    /// <summary>The user the reservation belongs to.</summary>
    public required string UserId { get; init; }

    /// <summary>Customer email (propagated for downstream events).</summary>
    public required string CustomerEmail { get; init; }

    /// <summary>The order's total amount (propagated for downstream payment session).</summary>
    public required long TotalAmountCents { get; init; }

    /// <summary>Order currency (propagated for downstream payment session).</summary>
    public required string Currency { get; init; }

    /// <summary>The line items to reserve.</summary>
    public required IReadOnlyList<Haworks.Contracts.Checkout.CheckoutItemData> Items { get; init; }

    /// <summary>Idempotency key from the original checkout request.</summary>
    public string? IdempotencyKey { get; init; }
}
