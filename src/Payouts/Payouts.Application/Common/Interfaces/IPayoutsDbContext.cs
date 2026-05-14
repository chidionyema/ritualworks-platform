using Haworks.Payouts.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payouts.Application.Common.Interfaces;

public interface IPayoutsDbContext
{
    DbSet<LedgerAccount> LedgerAccounts { get; }
    DbSet<LedgerEntry> LedgerEntries { get; }
    DbSet<Payout> Payouts { get; }
    DbSet<SellerProfile> SellerProfiles { get; }
    
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
