using Haworks.Payments.Domain;

namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Processes completed payment sessions (called from webhook handlers).
/// </summary>
public interface IPaymentSessionProcessor
{
    /// <summary>
    /// Handles a completed checkout session from a webhook.
    /// </summary>
    Task HandleCompletedSessionAsync(
        PaymentSessionEvent sessionEvent,
        CancellationToken ct = default);

    /// <summary>
    /// Validates a session against the database record.
    /// Used for redirect flow validation.
    /// </summary>
    Task<bool> ValidateSessionAsync(
        string sessionId,
        string userId,
        CancellationToken ct = default);
}

/// <summary>
/// Represents a completed payment session event from a webhook.
/// </summary>
public record PaymentSessionEvent
{
    public required string SessionId { get; init; }
    public required string TransactionId { get; init; }
    public required SessionMode Mode { get; init; }
    public required long AmountTotal { get; init; }
    public required string Currency { get; init; }
    public PaymentProvider Provider { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// The mode of a checkout session.
/// </summary>
public enum SessionMode
{
    Payment,
    Subscription,
    Setup
}
