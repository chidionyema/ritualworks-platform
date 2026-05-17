using Haworks.Privacy.Application.Common.Interfaces;
using Haworks.Privacy.Domain.Aggregates;
using Haworks.Privacy.Application.Requests.Sagas;
using Microsoft.EntityFrameworkCore;
using MassTransit;

namespace Haworks.Privacy.Infrastructure.Persistence;

public class PrivacyDbContext : DbContext, IPrivacyDbContext
{
    public PrivacyDbContext(DbContextOptions<PrivacyDbContext> options) : base(options) { }

    public DbSet<PrivacyRequest> PrivacyRequests => Set<PrivacyRequest>();
    public DbSet<PrivacyRequestStep> PrivacyRequestSteps => Set<PrivacyRequestStep>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema("privacy");

        builder.AddInboxStateEntity();
        builder.AddOutboxStateEntity();
        builder.AddOutboxMessageEntity();

        builder.Entity<PrivacyRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        builder.Entity<PrivacyRequestStep>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.RequestId);
            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        builder.Entity<PrivacyRequestState>(entity =>
        {
            entity.HasKey(e => e.CorrelationId);
            entity.Property(e => e.CurrentState).HasMaxLength(64);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Version).IsConcurrencyToken();
        });
    }
}
