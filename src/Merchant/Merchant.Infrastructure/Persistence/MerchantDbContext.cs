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
        builder.HasDefaultSchema("merchant");

        builder.AddInboxStateEntity();
        builder.AddOutboxStateEntity();
        builder.AddOutboxMessageEntity();

        builder.Entity<MerchantProfile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.OwnerId).IsUnique();
            entity.HasIndex(e => e.Slug).IsUnique();

            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Slug).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Bio).HasMaxLength(2000);
            entity.Property(e => e.LogoUrl).HasMaxLength(2048);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.ContactEmail).HasMaxLength(320);
            entity.Property(e => e.ContactPhone).HasMaxLength(50);
            entity.Property(e => e.Category).HasMaxLength(200);
            entity.Property(e => e.Website).HasMaxLength(2048);
            entity.Property(e => e.Timezone).HasMaxLength(50);
            entity.Property(e => e.RejectionReason).HasMaxLength(500);
            entity.Property(e => e.SuspensionReason).HasMaxLength(500);
            entity.Property(e => e.ApprovedBy).HasMaxLength(200);
            entity.Property(e => e.RejectedBy).HasMaxLength(200);
            entity.Property(e => e.SuspendedBy).HasMaxLength(200);
            entity.Property(e => e.DeactivatedBy).HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        });

        builder.Entity<OperatingHours>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MerchantId);
            entity.Property(e => e.IsOpen).HasDefaultValue(true);
        });
    }
}
