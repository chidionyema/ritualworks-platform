using Haworks.Location.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Location.Application.Interfaces;

public interface ILocationDbContext
{
    DbSet<Address> Addresses { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
