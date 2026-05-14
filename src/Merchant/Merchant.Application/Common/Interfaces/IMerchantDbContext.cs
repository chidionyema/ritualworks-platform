using Haworks.Merchant.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Merchant.Application.Common.Interfaces;

public interface IMerchantDbContext
{
    DbSet<MerchantProfile> Merchants { get; }
    DbSet<OperatingHours> OperatingHours { get; }
    
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
