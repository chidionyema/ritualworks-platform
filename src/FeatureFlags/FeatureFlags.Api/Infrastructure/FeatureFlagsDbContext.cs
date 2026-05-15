using Haworks.FeatureFlags.Api.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.FeatureFlags.Api.Infrastructure;

public class FeatureFlagsDbContext : DbContext
{
    public FeatureFlagsDbContext(DbContextOptions<FeatureFlagsDbContext> options) : base(options)
    {
    }

    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<FeatureFlagRule> Rules => Set<FeatureFlagRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("featureflags");

        modelBuilder.Entity<FeatureFlag>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<FeatureFlagRule>(entity =>
        {
            entity.HasKey(x => x.Id);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}
