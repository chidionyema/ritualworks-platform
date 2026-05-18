namespace Haworks.Contracts.Catalog;

/// <summary>
/// Published when stock is successfully reserved for an order.
///
/// This event is the bounded-context boundary between Catalog (stock state)
/// and Payments (next step in the flow). To respect that boundary the event
/// MUST carry every piece of data downstream consumers need so they don't
/// have to query foreign repositories. In particular, the Payments context's
/// PaymentSessionConsumer needs order-level data (TotalAmount, CustomerEmail,
/// LineItems with prices) — those fields are propagated here from the
/// CheckoutInitiatedEvent that triggered the stock reservation.
///
/// Subscribers:
/// - PaymentSessionConsumer (Payments) — creates the gateway session
/// - CheckoutSaga — transitions to StockHeld
/// - Metrics / dashboards — read-only
/// </summary>
public sealed record StockReservedEvent : DomainEvent
{
    /// <summary>The order the stock was reserved for.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga correlation ID for tracking the checkout flow.</summary>
    public required Guid SagaId { get; init; }

    /// <summary>The user the order belongs to (propagated from checkout).</summary>
    public required string UserId { get; init; }

    /// <summary>The total amount due for the order, including tax.</summary>
    public required long TotalAmountCents { get; init; }

    /// <summary>The order currency (e.g., "USD").</summary>
    public required string Currency { get; init; }

    /// <summary>Customer email for the payment gateway session and notifications.</summary>
    public required string CustomerEmail { get; init; }

    /// <summary>Idempotency key from checkout, used by the payment gateway.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>The reserved items (Catalog-side view: name, quantity, remaining stock).</summary>
    public required IReadOnlyList<StockReservationItem> Items { get; init; }

    /// <summary>
    /// The order-line items with unit prices, propagated from the original
    /// checkout event. Used by the Payments consumer to build the payment
    /// gateway request without having to read OrderDbContext.
    /// </summary>
    public required IReadOnlyList<Haworks.Contracts.Checkout.CheckoutItemData> OrderLineItems { get; init; }

    /// <summary>Total number of units reserved across all items.</summary>
    public int TotalUnitsReserved => Items.Sum(i => i.Quantity);
}
