using Haworks.FeatureFlags.Api.Domain;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Haworks.FeatureFlags.Api.Infrastructure;

public class FeatureFlagsDbContext : DbContext
{
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public FeatureFlagsDbContext(DbContextOptions<FeatureFlagsDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
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

            // xmin concurrency token
            entity.Property(x => x.RowVersion)
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<FeatureFlagRule>(entity =>
        {
            entity.HasKey(x => x.Id);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var userId = _httpContextAccessor?.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? _httpContextAccessor?.HttpContext?.User?.FindFirstValue("sub");
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<FeatureFlag>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.CreatedBy = userId;
                entry.Entity.UpdatedAt = now;
                entry.Entity.UpdatedBy = userId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                entry.Entity.UpdatedBy = userId;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
