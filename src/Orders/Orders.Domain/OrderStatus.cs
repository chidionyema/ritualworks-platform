namespace Haworks.Orders.Domain;

/// <summary>
/// orders-svc state machine. Tighter than the monolith's enum because
/// orders-svc isn't responsible for stock or payment session lifecycle —
/// just for the Order aggregate's own progression. Other states (Refunded,
/// PartiallyRefunded, Disputed, ...) land in later phases when warranted.
/// </summary>
public enum OrderStatus
{
    /// <summary>Order created, awaiting payment / fulfillment events.</summary>
    Created = 0,

    /// <summary>Payment received (consumed PaymentCompletedEvent).</summary>
    Paid = 1,

    /// <summary>Order didn't complete (consumed PaymentSessionFailed or StockReservationFailed).</summary>
    Abandoned = 2,

    /// <summary>Stripe checkout session expired before completion.</summary>
    Expired = 3,

    /// <summary>Order has been refunded.</summary>
    Refunded = 4,
}
