namespace Haworks.Contracts.Payments;

/// <summary>
/// Published when payment session creation fails.
/// After max retries, triggers stock compensation.
/// Consumers:
/// - StockReleaseConsumer: Releases reserved stock (after max retries)
/// - OrderStatusConsumer: Updates order status to PaymentFailed
/// - SignalRNotifier: Notifies user of failure
/// - AlertConsumer: Triggers alerts for ops team
/// </summary>
public sealed record PaymentSessionFailedEvent : DomainEvent
{
    /// <summary>The order that failed payment session creation.</summary>
    public required Guid OrderId { get; init; }

    /// <summary>The saga identifier for correlation.</summary>
    public required Guid SagaId { get; init; }

    /// <summary>The payment provider that failed.</summary>
    public required string Provider { get; init; }

    /// <summary>Error code from the provider or internal.</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Human-readable error message.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Which retry attempt this was (1-based).</summary>
    public int AttemptNumber { get; init; }

    /// <summary>Whether this is the final attempt (max retries reached).</summary>
    public bool IsFinalAttempt { get; init; }
}
