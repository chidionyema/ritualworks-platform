using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Orders.Infrastructure;

/// <summary>
/// DbContext for the Orders bounded context. Owns Orders + OrderItems +
/// MassTransit transactional outbox tables. Per ADR-0009 no entities
/// reference types from other contexts — UserId is opaque string FK,
/// PaymentId/ProductId are opaque Guids.
/// </summary>
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
            entity.Property(o => o.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");

            // xmin shadow concurrency token — same pattern as catalog/payments.
            // Catches concurrent state transitions on the same Order
            // (e.g., StockReservationFailed and PaymentSessionFailed both
            // arriving for the same Created order — first one wins, second
            // hits DbUpdateConcurrencyException → MT retries → sees the
            // already-Abandoned order and bails idempotently).
            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(o => o.UserId).HasDatabaseName("IX_Orders_UserId");
            entity.HasIndex(o => o.SagaId).IsUnique().HasDatabaseName("IX_Orders_SagaId");
            entity.HasIndex(o => o.IdempotencyKey).HasDatabaseName("IX_Orders_IdempotencyKey");
            entity.HasIndex(o => o.Status).HasDatabaseName("IX_Orders_Status");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.ToTable("OrderItems");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.ProductName).HasMaxLength(200).IsRequired();
            entity.Property(i => i.UnitPrice).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(i => i.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");

            entity.HasOne(i => i.Order)
                .WithMany(o => o.Items)
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(i => i.OrderId).HasDatabaseName("IX_OrderItems_OrderId");
            entity.HasIndex(i => i.ProductId).HasDatabaseName("IX_OrderItems_ProductId");
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return await base.SaveChangesAsync(cancellationToken);
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
