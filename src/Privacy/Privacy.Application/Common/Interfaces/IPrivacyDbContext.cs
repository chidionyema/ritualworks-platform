using Haworks.Privacy.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Privacy.Application.Common.Interfaces;

public interface IPrivacyDbContext
{
    DbSet<PrivacyRequest> PrivacyRequests { get; }
    DbSet<PrivacyRequestStep> PrivacyRequestSteps { get; }
    
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
