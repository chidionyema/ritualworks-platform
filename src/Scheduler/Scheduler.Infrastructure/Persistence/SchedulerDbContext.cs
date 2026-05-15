using MassTransit;
using Microsoft.EntityFrameworkCore;
using Haworks.Scheduler.Application.Common.Interfaces;

namespace Haworks.Scheduler.Infrastructure.Persistence;

public class SchedulerDbContext : DbContext
{
    public SchedulerDbContext(DbContextOptions<SchedulerDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("scheduler");
        builder.AddInboxStateEntity();
        builder.AddOutboxStateEntity();
        builder.AddOutboxMessageEntity();
    }
}
