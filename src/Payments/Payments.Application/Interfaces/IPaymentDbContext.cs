using Haworks.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payments.Application.Interfaces;

public interface IPaymentDbContext
{
    DbSet<Payment> Payments { get; }
    DbSet<Subscription> Subscriptions { get; }
    DbSet<RefundSagaState> RefundSagas { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
