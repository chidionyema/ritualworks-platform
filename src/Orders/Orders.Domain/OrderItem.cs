namespace Haworks.Orders.Domain;

/// <summary>
/// A single line on an Order. Per ADR-0009 carries no navigation to
/// catalog-svc's Product — instead snapshots ProductName + UnitPrice
/// at order time so order history is queryable without a cross-context
/// join. Catalog's `ProductUpdatedEvent` (Phase 4 read model) keeps the
/// snapshot fresh for active orders if needed.
/// </summary>
public class OrderItem : AuditableEntity
{
    /// <summary>EF Core materialization constructor.</summary>
    protected OrderItem() : base() { }

    private OrderItem(Guid orderId, Guid productId, string productName, int quantity, decimal unitPrice)
        : base()
    {
        OrderId = orderId;
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public Guid OrderId { get; private set; }
    public Order? Order { get; private set; }

    public Guid ProductId { get; private set; }                       // opaque FK -> catalog-svc
    public string ProductName { get; private set; } = string.Empty;   // snapshot at order time
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }                    // snapshot at order time

    public decimal LineTotal => Quantity * UnitPrice;

    public static OrderItem Create(Guid orderId, Guid productId, string productName, int quantity, decimal unitPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        if (orderId == Guid.Empty)   throw new ArgumentException("OrderId required", nameof(orderId));
        if (productId == Guid.Empty) throw new ArgumentException("ProductId required", nameof(productId));
        if (quantity <= 0)           throw new ArgumentException("Quantity must be positive", nameof(quantity));
        if (unitPrice < 0)           throw new ArgumentException("UnitPrice cannot be negative", nameof(unitPrice));

        return new OrderItem(orderId, productId, productName, quantity, unitPrice);
    }
}
