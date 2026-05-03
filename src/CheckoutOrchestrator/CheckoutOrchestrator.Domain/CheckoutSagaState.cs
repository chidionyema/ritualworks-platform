using MassTransit;

namespace Haworks.CheckoutOrchestrator.Domain;

/// <summary>
/// Persistent saga state for the CheckoutSaga state machine. Holds:
///   • <see cref="CorrelationId"/> — the SagaId, used everywhere across
///     the choreography for cross-service correlation.
///   • <see cref="CurrentState"/> — MassTransit's built-in column for the
///     current state-machine state name.
///   • Snapshot of the data the saga needs to act on each event without
///     re-querying foreign repositories (UserId, CustomerEmail, TotalAmount,
///     line items, etc.). Per ADR-0009 the saga owns no business state —
///     this snapshot is just enough to drive the orchestration.
///   • RowVersion (uint via xmin) for optimistic concurrency on saga state.
///
/// Implements <see cref="ISagaVersion"/> so MassTransit's EF persister
/// uses the Version column for write-time conflict detection (in addition
/// to xmin).
/// </summary>
public class CheckoutSagaState : SagaStateMachineInstance, ISagaVersion
{
    /// <summary>SagaId — the cross-service correlation key.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>Current state-machine state (e.g., "Initiated", "ReadyForPayment", "Completed").</summary>
    public string CurrentState { get; set; } = string.Empty;

    /// <summary>MT optimistic concurrency token (separate from xmin — both layers).</summary>
    public int Version { get; set; }

    /// <summary>The order this saga is driving.</summary>
    public Guid OrderId { get; set; }

    public string UserId { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string? IdempotencyKey { get; set; }

    /// <summary>JSON-serialized line items snapshot (passed through to downstream events).</summary>
    public string LineItemsJson { get; set; } = "[]";

    /// <summary>Set after stock is reserved — needed for the compensation path's StockReleaseRequested.</summary>
    public string? ReservedItemsJson { get; set; }

    /// <summary>Set after payment session is created — for ops/debug visibility.</summary>
    public Guid? PaymentId { get; set; }
    public string? PaymentSessionId { get; set; }
    public string? PaymentCheckoutUrl { get; set; }

    /// <summary>Why the saga ended in a non-Completed state. Audit + ops.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Token for the payment-expiry timeout schedule. MassTransit
    /// uses this to cancel the timeout when payment lands in time.</summary>
    public Guid? PaymentExpiryTokenId { get; set; }

    /// <summary>When the saga was kicked off — used for the OrderAbandoned age calculation.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
