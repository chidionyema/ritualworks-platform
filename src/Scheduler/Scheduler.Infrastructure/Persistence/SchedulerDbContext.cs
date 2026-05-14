using Microsoft.EntityFrameworkCore;
using Haworks.Scheduler.Application.Common.Interfaces;

namespace Haworks.Scheduler.Infrastructure.Persistence;

public class SchedulerDbContext : DbContext
{
    public SchedulerDbContext(DbContextOptions<SchedulerDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // No custom business entities yet, but we need the Outbox tables.
    }
}
