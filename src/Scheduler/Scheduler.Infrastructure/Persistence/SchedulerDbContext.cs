using Haworks.Scheduler.Domain;
using Haworks.Scheduler.Domain.Entities;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Scheduler.Infrastructure.Persistence;

public class SchedulerDbContext : DbContext
{
    public SchedulerDbContext(DbContextOptions<SchedulerDbContext> options) : base(options) { }

    public DbSet<ScheduledEvent> ScheduledEvents => Set<ScheduledEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("scheduler");
        builder.AddInboxStateEntity();
        builder.AddOutboxStateEntity();
        builder.AddOutboxMessageEntity();

        builder.Entity<ScheduledEvent>(entity =>
        {
            entity.ToTable("scheduled_events");
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.IdempotencyKey)
                .IsUnique();

            entity.Property(e => e.IdempotencyKey)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(e => e.TargetExchange).HasMaxLength(255).IsRequired();
            entity.Property(e => e.RoutingKey).HasMaxLength(255).IsRequired();
            entity.Property(e => e.ScheduledBy).HasMaxLength(200).IsRequired();
            entity.Property(e => e.HangfireJobId).HasMaxLength(200).IsRequired();

            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });
    }
}
