using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.Orders.Domain;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Orders.Infrastructure;

public class OrderDbContext : DbContext
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICurrentUserService _currentUserService;

    public OrderDbContext(
        DbContextOptions<OrderDbContext> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        ICurrentUserService currentUserService)
        : base(options)
    {
        _environment = environment;
        _loggerFactory = loggerFactory;
        _currentUserService = currentUserService;
        ChangeTracker.LazyLoadingEnabled = false;
    }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<GuestOrderInfo> GuestOrders => Set<GuestOrderInfo>();
    public DbSet<StockReleaseFailure> StockReleaseFailures => Set<StockReleaseFailure>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseLoggerFactory(_loggerFactory);
        if (_environment.IsDevelopment()) optionsBuilder.EnableSensitiveDataLogging();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("orders");

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(o => o.Id);

            entity.Property(o => o.UserId).HasMaxLength(450).IsRequired();
            entity.Property(o => o.SagaId).IsRequired();
            entity.Property(o => o.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(o => o.CustomerEmail).HasMaxLength(254).IsRequired();
            entity.Property(o => o.TotalAmount).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(o => o.Currency).HasMaxLength(3).IsRequired();
            entity.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(o => o.AbandonReason).HasMaxLength(500);

            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(o => o.UserId).HasDatabaseName("IX_Orders_UserId");
            entity.HasIndex(o => o.SagaId).IsUnique().HasDatabaseName("IX_Orders_SagaId");
            entity.HasIndex(o => o.IdempotencyKey).HasDatabaseName("IX_Orders_IdempotencyKey");
            entity.HasIndex(o => o.Status).HasDatabaseName("IX_Orders_Status");

            entity.HasMany(typeof(OrderStatusHistory), "_statusHistory")
                .WithOne()
                .HasForeignKey("OrderId")
                .OnDelete(DeleteBehavior.Cascade);

            entity.Navigation("_statusHistory").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OrderStatusHistory>(entity =>
        {
            entity.ToTable("OrderStatusHistory");
            entity.HasKey(h => h.Id);
            entity.Property(h => h.ChangedBy).HasMaxLength(200);
            entity.Property(h => h.Reason).HasMaxLength(500);
            entity.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(50);
            entity.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(50);
            entity.HasIndex(h => h.OrderId).HasDatabaseName("IX_OrderStatusHistory_OrderId");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.ProductName).HasMaxLength(200).IsRequired();
            entity.Property(i => i.UnitPrice).HasColumnType("numeric(18,2)").IsRequired();

            entity.HasOne(i => i.Order)
                .WithMany(o => o.Items)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(i => i.OrderId).HasDatabaseName("IX_OrderItems_OrderId");
            entity.HasIndex(i => i.ProductId).HasDatabaseName("IX_OrderItems_ProductId");
        });

        modelBuilder.Entity<GuestOrderInfo>(entity =>
        {
            entity.ToTable("GuestOrders");
            entity.HasKey(g => g.Id);
            entity.Property(g => g.Email).HasMaxLength(254);
            entity.Property(g => g.FirstName).HasMaxLength(100);
            entity.Property(g => g.LastName).HasMaxLength(100);
            entity.Property(g => g.Address).HasMaxLength(500);
            entity.Property(g => g.City).HasMaxLength(100);
            entity.Property(g => g.State).HasMaxLength(100);
            entity.Property(g => g.PostalCode).HasMaxLength(20);
            entity.Property(g => g.Country).HasMaxLength(100);
            entity.Property(g => g.Phone).HasMaxLength(30);
            entity.Property(g => g.OrderToken).HasMaxLength(500).IsRequired();

            entity.HasOne(g => g.Order)
                .WithOne()
                .HasForeignKey<GuestOrderInfo>(g => g.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            
            entity.HasIndex(g => g.OrderToken).IsUnique().HasDatabaseName("IX_GuestOrders_Token");
        });

        modelBuilder.Entity<StockReleaseFailure>(entity =>
        {
            entity.ToTable("StockReleaseFailures");
            entity.HasKey(f => f.Id);
            entity.Property(f => f.ErrorMessage).HasMaxLength(2000);
            
            entity.OwnsMany(f => f.Items, items =>
            {
                items.ToTable("StockReleaseFailureItems");
                items.WithOwner().HasForeignKey("StockReleaseFailureId");
                items.Property<Guid>("Id");
                items.HasKey("Id");
            });
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void StampAuditFields()
    {
        var entries = ChangeTracker.Entries<AuditableEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified);
        foreach (var entry in entries)
        {
            entry.Entity.LastModifiedDate = DateTime.UtcNow;
            entry.Entity.LastModifiedBy = _currentUserService.UserId ?? "system";
            entry.Entity.ModifiedFromIp = _currentUserService.ClientIp ?? "unknown";
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = DateTime.UtcNow;
                entry.Entity.CreatedBy = _currentUserService.UserId ?? "system";
                entry.Entity.CreatedFromIp = _currentUserService.ClientIp ?? "unknown";
            }
        }
    }
}
