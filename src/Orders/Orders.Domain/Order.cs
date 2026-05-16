namespace Haworks.Orders.Domain;

/// <summary>
/// orders-svc Order aggregate. Per ADR-0009 carries no cross-context
/// references — no Payment navigation, no UserProfile navigation. UserId is
/// an opaque string FK to identity-svc; PaymentId (when populated by the
/// PaymentCompletedConsumer) is an opaque Guid pointer to payments-svc'
/// Payment aggregate.
///
/// State machine (Phase 4):
///   Created --(PaymentCompletedEvent)-->            Paid
///   Created --(PaymentSessionFailedEvent)-->        Abandoned
///   Created --(StockReservationFailedEvent)-->      Abandoned
///
/// Idempotency at the application level: the consumer checks Status before
/// transitioning, so duplicate webhook redeliveries (and MT inbox-deduped
/// replays) don't double-publish OrderCompleted/OrderAbandoned.
/// </summary>
public class Order : AuditableEntity
{
    private readonly List<OrderItem> _items = new();
    private readonly List<OrderStatusHistory> _statusHistory = new();

    /// <summary>EF Core materialization constructor.</summary>
    protected Order() : base() { }

    private Order(string userId, decimal totalAmount, string currency, Guid sagaId, string idempotencyKey, string customerEmail)
        : base()
    {
        UserId = userId;
        TotalAmount = totalAmount;
        Currency = currency;
        SagaId = sagaId;
        IdempotencyKey = idempotencyKey;
        CustomerEmail = customerEmail;
        Status = OrderStatus.Created;
    }

    public string UserId { get; private set; } = string.Empty;        // opaque FK -> identity-svc
    public Guid SagaId { get; private set; }                          // checkout saga correlation
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string CustomerEmail { get; private set; } = string.Empty;

    public decimal TotalAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public OrderStatus Status { get; private set; } = OrderStatus.Created;

    /// <summary>Set when transitioning to Paid (consumer of PaymentCompletedEvent).</summary>
    public Guid? PaymentId { get; private set; }

    /// <summary>Set when transitioning to Abandoned. Carries the upstream reason for ops/audit.</summary>
    public string? AbandonReason { get; private set; }

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();
    public IReadOnlyCollection<OrderStatusHistory> StatusHistory => _statusHistory.AsReadOnly();

    public static Order Create(
        string userId,
        decimal totalAmount,
        string currency,
        Guid sagaId,
        string idempotencyKey,
        string customerEmail,
        IEnumerable<(Guid productId, string productName, int quantity, decimal unitPrice)> lineItems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerEmail);
        if (totalAmount < 0)      throw new ArgumentException("TotalAmount cannot be negative", nameof(totalAmount));
        if (sagaId == Guid.Empty) throw new ArgumentException("SagaId required", nameof(sagaId));

        var order = new Order(userId, totalAmount, currency, sagaId, idempotencyKey, customerEmail);
        foreach (var li in lineItems)
        {
            order._items.Add(OrderItem.Create(order.Id, li.productId, li.productName, li.quantity, li.unitPrice));
        }
        if (order._items.Count == 0)
        {
            throw new ArgumentException("Order must have at least one line item", nameof(lineItems));
        }
        return order;
    }

    /// <summary>
    /// Transitions to Paid. Returns false if the order is already in a terminal
    /// state (Paid or Abandoned) — caller should treat that as a no-op idempotent
    /// outcome rather than throw, matching how the PaymentCompletedConsumer handles
    /// duplicate webhook redeliveries.
    /// </summary>
    public bool MarkPaid(Guid paymentId, string? changedBy = null)
    {
        if (paymentId == Guid.Empty) throw new ArgumentException("PaymentId required", nameof(paymentId));
        if (Status != OrderStatus.Created) return false;
        var previousStatus = Status;
        Status = OrderStatus.Paid;
        PaymentId = paymentId;
        LastModifiedDate = DateTime.UtcNow;
        _statusHistory.Add(OrderStatusHistory.Create(Id, previousStatus, Status, changedBy));
        return true;
    }

    /// <summary>Transitions to Abandoned. Same idempotent-no-op semantics as MarkPaid.</summary>
    public bool MarkAbandoned(string reason, string? changedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status != OrderStatus.Created) return false;
        var previousStatus = Status;
        Status = OrderStatus.Abandoned;
        AbandonReason = reason;
        LastModifiedDate = DateTime.UtcNow;
        _statusHistory.Add(OrderStatusHistory.Create(Id, previousStatus, Status, changedBy, reason));
        return true;
    }

    /// <summary>Transitions to Expired. Same idempotent-no-op semantics as MarkPaid.</summary>
    public bool MarkExpired(string reason, string? changedBy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status != OrderStatus.Created) return false;
        var previousStatus = Status;
        Status = OrderStatus.Expired;
        AbandonReason = reason;
        LastModifiedDate = DateTime.UtcNow;
        _statusHistory.Add(OrderStatusHistory.Create(Id, previousStatus, Status, changedBy, reason));
        return true;
    }

    /// <summary>Transitions to Refunded. Only allowed from Paid state.</summary>
    public bool MarkRefunded(string? changedBy = null, string? reason = null)
    {
        if (Status != OrderStatus.Paid) return false;
        var previousStatus = Status;
        Status = OrderStatus.Refunded;
        LastModifiedDate = DateTime.UtcNow;
        _statusHistory.Add(OrderStatusHistory.Create(Id, previousStatus, Status, changedBy, reason));
        return true;
    }

    /// <summary>
    /// Anonymises PII fields for GDPR erasure. Idempotent — safe to call
    /// multiple times on the same order.
    /// </summary>
    public void AnonymiseForPrivacy()
    {
        UserId = "ERASED";
        CustomerEmail = "deleted@privacy.invalid";
        LastModifiedDate = DateTime.UtcNow;
    }

    /// <summary>Reverts to Paid status. Usually after a failed or cancelled refund.</summary>
    public bool RevertToPaid(string? changedBy = null, string? reason = null)
    {
        if (Status != OrderStatus.Refunded) return false;
        var previousStatus = Status;
        Status = OrderStatus.Paid;
        LastModifiedDate = DateTime.UtcNow;
        _statusHistory.Add(OrderStatusHistory.Create(Id, previousStatus, Status, changedBy, reason));
        return true;
    }
}
