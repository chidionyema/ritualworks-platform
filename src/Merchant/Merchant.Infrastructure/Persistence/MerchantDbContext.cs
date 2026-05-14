using Haworks.Merchant.Application.Common.Interfaces;
using Haworks.Merchant.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using MassTransit;

namespace Haworks.Merchant.Infrastructure.Persistence;

public class MerchantDbContext : DbContext, IMerchantDbContext
{
    public MerchantDbContext(DbContextOptions<MerchantDbContext> options) : base(options) { }

    public DbSet<MerchantProfile> Merchants => Set<MerchantProfile>();
    public DbSet<OperatingHours> OperatingHours => Set<OperatingHours>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.AddInboxStateEntity();
        builder.AddOutboxStateEntity();
        builder.AddOutboxMessageEntity();

        builder.Entity<MerchantProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => e.Slug).IsUnique();
        });

        builder.Entity<OperatingHours>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MerchantId);
        });
    }
}
