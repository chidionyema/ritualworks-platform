using Microsoft.EntityFrameworkCore;

namespace Haworks.Payments.Infrastructure.Repositories;

internal sealed class PaymentRepository(PaymentDbContext db) : IPaymentRepository
{
    public Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Payment?> GetByIdTrackedAsync(Guid id, CancellationToken ct = default) =>
        db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Payment?> GetByProviderSessionAsync(
        PaymentProvider provider, string providerSessionId, CancellationToken ct = default) =>
        db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Provider == provider && p.ProviderSessionId == providerSessionId, ct);

    public Task<Payment?> GetByProviderSessionTrackedAsync(
        PaymentProvider provider, string providerSessionId, CancellationToken ct = default) =>
        db.Payments
            .FirstOrDefaultAsync(p => p.Provider == provider && p.ProviderSessionId == providerSessionId, ct);

    public Task<Payment?> GetByOrderIdTrackedAsync(Guid orderId, CancellationToken ct = default) =>
        db.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);

    public async Task AddAsync(Payment payment, CancellationToken ct = default)
    {
        await db.Payments.AddAsync(payment, ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        db.SaveChangesAsync(ct);
}
