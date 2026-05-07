namespace Haworks.Payments.Application.Interfaces;

/// <summary>
/// Result of a cached session validation lookup.
/// </summary>
public sealed record SessionValidationResult(Guid OrderId, string UserId);

/// <summary>
/// Manages distributed cache for payment session validation.
/// </summary>
public interface IPaymentSessionCache
{
    Task<SessionValidationResult?> GetAsync(string sessionId, CancellationToken ct = default);
    Task SetAsync(string sessionId, Guid orderId, string userId, CancellationToken ct = default);
    Task RemoveAsync(string sessionId, CancellationToken ct = default);
    bool ValidateOwnership(SessionValidationResult cached, string userId);
}
