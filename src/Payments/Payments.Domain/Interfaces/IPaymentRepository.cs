namespace Haworks.Payments.Domain.Interfaces;

/// <summary>
/// Payments-context repository for the Payment aggregate.
/// </summary>
public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Payment?> GetByIdTrackedAsync(Guid id, CancellationToken ct = default);
    Task<Payment?> GetByProviderSessionAsync(PaymentProvider provider, string providerSessionId, CancellationToken ct = default);
    /// <summary>Tracked variant for the webhook consumer (mutates + saves).</summary>
    Task<Payment?> GetByProviderSessionTrackedAsync(PaymentProvider provider, string providerSessionId, CancellationToken ct = default);
    Task<Payment?> GetByOrderIdTrackedAsync(Guid orderId, CancellationToken ct = default);
    Task AddAsync(Payment payment, CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
