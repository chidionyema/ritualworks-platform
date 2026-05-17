using Haworks.Shipping.Api.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Shipping.Api.Infrastructure;

public class ShippingDbContext : DbContext
{
    public ShippingDbContext(DbContextOptions<ShippingDbContext> options) : base(options) { }

    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("shipping");

        modelBuilder.Entity<Shipment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.TrackingNumber);
            e.HasIndex(x => x.EasyPostShipmentId).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.RateAmount).HasColumnType("numeric(18,2)");
            e.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}
