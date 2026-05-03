namespace Haworks.Payments.Domain;

/// <summary>
/// Lifecycle states of a Payment aggregate.
/// </summary>
public enum PaymentStatus
{
    Pending,
    Processing,
    Completed,
    Failed,
    Refunded,
    Cancelled,
    Flagged // requires manual review (e.g., amount mismatch on webhook)
}
