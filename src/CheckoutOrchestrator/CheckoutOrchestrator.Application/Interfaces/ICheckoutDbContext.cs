using Haworks.CheckoutOrchestrator.Application.Sagas;
using Microsoft.EntityFrameworkCore;

namespace Haworks.CheckoutOrchestrator.Application.Interfaces;

public interface ICheckoutDbContext
{
    DbSet<CheckoutSagaState> CheckoutSagas { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
