namespace Haworks.Payments.Domain;

/// <summary>
/// Payment aggregate. Owns its full lifecycle from session creation through
/// provider webhook completion.
///
/// Per ADR-0009 (DB-per-service), Payment carries no cross-context refs —
/// no Order navigation, no User navigation. <see cref="OrderId"/> is an
/// opaque foreign key to orders-svc; orders-svc maintains its own payment
/// snapshot via the <c>PaymentCompletedEvent</c> consumer (Phase 4).
/// <see cref="UserId"/> is an opaque string FK to identity-svc.
/// </summary>
public class Payment : AuditableEntity
{
    /// <summary>EF Core materialization constructor.</summary>
    protected Payment() : base() { }

    private Payment(
        Guid orderId,
        string userId,
        decimal amount,
        decimal tax,
        string currency,
        PaymentProvider provider,
        Guid sagaId)
        : base()
    {
        OrderId = orderId;
        UserId = userId;
        Amount = amount;
        Tax = tax;
        Currency = currency;
        Provider = provider;
        SagaId = sagaId;
        Status = PaymentStatus.Pending;
    }

    public Guid OrderId { get; private set; }                         // opaque FK -> orders-svc
    public string UserId { get; private set; } = string.Empty;        // opaque FK -> identity-svc
    public Guid SagaId { get; private set; }                          // checkout saga correlation

    public decimal Amount { get; private set; }
    public decimal Tax { get; private set; }
    public string Currency { get; private set; } = "USD";

    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;
    public string PaymentMethod { get; private set; } = string.Empty;
    public bool IsComplete { get; private set; }

    public PaymentProvider Provider { get; private set; } = PaymentProvider.Stripe;
    public string? ProviderSessionId { get; private set; }
    public string? ProviderCheckoutUrl { get; private set; }
    public string? ProviderTransactionId { get; private set; }

    public static Payment Create(
        Guid orderId,
        string userId,
        decimal amount,
        decimal tax,
        string currency,
        PaymentProvider provider,
        Guid sagaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        if (amount < 0) throw new ArgumentException("Amount cannot be negative", nameof(amount));
        if (tax < 0)    throw new ArgumentException("Tax cannot be negative", nameof(tax));
        if (orderId == Guid.Empty) throw new ArgumentException("OrderId required", nameof(orderId));
        if (sagaId == Guid.Empty)  throw new ArgumentException("SagaId required", nameof(sagaId));
        if (provider == PaymentProvider.None)
            throw new ArgumentException("A concrete PaymentProvider must be supplied", nameof(provider));

        return new Payment(orderId, userId, amount, tax, currency, provider, sagaId);
    }

    /// <summary>Sets the provider session/checkout URL after gateway createSession.</summary>
    public void AttachProviderSession(string sessionId, string? checkoutUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ProviderSessionId = sessionId;
        ProviderCheckoutUrl = checkoutUrl;
        Status = PaymentStatus.Processing;
        LastModifiedDate = DateTime.UtcNow;
    }

    /// <summary>Marks the payment as completed (called from the webhook consumer).</summary>
    public void MarkCompleted(string providerTransactionId, string paymentMethod)
    {
        if (IsComplete)
            throw new InvalidOperationException("Payment is already completed");
        if (Status != PaymentStatus.Processing && Status != PaymentStatus.Pending)
            throw new InvalidOperationException($"Cannot complete a payment with status {Status}");

        ArgumentException.ThrowIfNullOrWhiteSpace(providerTransactionId);
        ProviderTransactionId = providerTransactionId;
        PaymentMethod = paymentMethod ?? string.Empty;
        IsComplete = true;
        Status = PaymentStatus.Completed;
        LastModifiedDate = DateTime.UtcNow;
    }

    /// <summary>Marks the payment as failed (gateway rejected, session expired, etc.).</summary>
    public void MarkFailed()
    {
        IsComplete = false;
        Status = PaymentStatus.Failed;
        LastModifiedDate = DateTime.UtcNow;
    }

    /// <summary>
    /// Flags for manual review (e.g., webhook reports an amount that doesn't match
    /// the originally-authorized amount). Distinct from Failed because the money
    /// may still be captured — operations decides what to do.
    /// </summary>
    public void Flag()
    {
        Status = PaymentStatus.Flagged;
        LastModifiedDate = DateTime.UtcNow;
    }

    /// <summary>Marks the payment as cancelled (user abandoned checkout).</summary>
    /// <summary>Marks the payment as refunded.</summary>
    public void MarkRefunded()
    {
        if (Status != PaymentStatus.Completed)
            throw new InvalidOperationException($"Cannot refund a payment with status {Status}");

        Status = PaymentStatus.Refunded;
        LastModifiedDate = DateTime.UtcNow;
    }

    public void MarkCancelled()
    {
        Status = PaymentStatus.Cancelled;
        LastModifiedDate = DateTime.UtcNow;
    }
}
