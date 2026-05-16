using Haworks.Payments.Domain;

namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Provider-agnostic interface for checkout session operations.
/// </summary>
public interface ICheckoutSessionService
{
    /// <summary>
    /// Creates a checkout session for one-time payment.
    /// </summary>
    Task<CheckoutSessionResult> CreateSessionAsync(
        CreateCheckoutSessionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves an existing session by ID.
    /// </summary>
    Task<CheckoutSession?> GetSessionAsync(
        string sessionId,
        CancellationToken ct = default);

    /// <summary>
    /// Expires/cancels a session.
    /// </summary>
    Task<bool> ExpireSessionAsync(
        string sessionId,
        CancellationToken ct = default);
}

/// <summary>
/// Request to create a one-time payment checkout session.
/// </summary>
public record CreateCheckoutSessionRequest
{
    public required string SuccessUrl { get; init; }
    public required string CancelUrl { get; init; }
    public required string CustomerEmail { get; init; }
    public required string IdempotencyKey { get; init; }
    public required IReadOnlyList<LineItem> LineItems { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public string Currency { get; init; } = "USD";
    public string? CustomerId { get; init; }
    public string? UserId { get; init; }
    public Guid? OrderId { get; init; }
}

/// <summary>
/// A line item in a checkout session.
/// </summary>
public record LineItem
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required long UnitAmountCents { get; init; }
    public required int Quantity { get; init; }
    public string Currency { get; init; } = "USD";
}

/// <summary>
/// Result of creating a checkout session.
/// </summary>
public record CheckoutSessionResult
{
    public required string SessionId { get; init; }
    public required string SessionUrl { get; init; }
    public string? TransactionId { get; init; }
    public PaymentProvider Provider { get; init; }
}

/// <summary>
/// Represents a checkout session state.
/// </summary>
public record CheckoutSession
{
    public required string SessionId { get; init; }
    public required SessionStatus Status { get; init; }
    public string? TransactionId { get; init; }
    public string? CustomerId { get; init; }
    public long? AmountTotal { get; init; }
    public string? Currency { get; init; }
    public PaymentProvider Provider { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Status of a checkout session.
/// </summary>
public enum SessionStatus
{
    Open,
    Complete,
    Expired
}
