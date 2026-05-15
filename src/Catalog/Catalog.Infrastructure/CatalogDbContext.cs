using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Catalog.Infrastructure;

/// <summary>
/// DbContext for the Catalog bounded context. Owns Categories, Products, and
/// ProductReviews. Per ADR-0009 (DB-per-service), no entities reference
/// types from other contexts (User, Order, Content) — UserId on
/// ProductReview is an opaque string FK to identity-svc.
/// </summary>
public class CatalogDbContext : DbContext
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICurrentUserService _currentUserService;

    public CatalogDbContext(
        DbContextOptions<CatalogDbContext> options,
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

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductReview> ProductReviews => Set<ProductReview>();
    public DbSet<ProductMetadata> ProductMetadata => Set<ProductMetadata>();
    public DbSet<ProductSpecification> ProductSpecifications => Set<ProductSpecification>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.UseLoggerFactory(_loggerFactory);

        if (_environment.IsDevelopment())
        {
            optionsBuilder.EnableSensitiveDataLogging();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("catalog");

        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("Categories");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Name).HasMaxLength(200).IsRequired();
            entity.Property(c => c.Description).HasMaxLength(2000);
            // Note: AuditableEntity carries a byte[] RowVersion field, but
            // EF's IsRowVersion() on Postgres tells EF to skip writes (relying
            // on a DB-managed timestamp that doesn't exist on bytea). Result:
            // NOT NULL violation on insert. Phase 2c switches optimistic
            // concurrency to Postgres's native `xmin` system column on the
            // entities that need it (Product for stock reservation).
            entity.Property(c => c.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");
            entity.HasIndex(c => c.Name).IsUnique().HasDatabaseName("IX_Categories_Name");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).HasMaxLength(200).IsRequired();
            entity.Property(p => p.Description).HasMaxLength(4000);
            entity.Property(p => p.UnitPrice).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(p => p.StockQuantity).IsRequired();
            entity.Property(p => p.IsInStock).IsRequired();
            entity.Property(p => p.IsListed).IsRequired();
            entity.Property(p => p.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");

            // Optimistic concurrency on stock reservation. Postgres exposes
            // its row-version equivalent (xmin) as a system column on every
            // table; declare a shadow property so EF can use it as the
            // concurrency token without us adding an application-managed
            // column. Two concurrent reservers race on
            // UPDATE ... WHERE xmin = N — the loser throws
            // DbUpdateConcurrencyException and the caller retries against
            // the already-decremented stock count.
            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasOne(p => p.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(p => p.CategoryId).HasDatabaseName("IX_Products_CategoryId");
            entity.HasIndex(p => p.IsListed).HasDatabaseName("IX_Products_IsListed");
        });

        modelBuilder.Entity<ProductReview>(entity =>
        {
            entity.ToTable("ProductReviews");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.UserId).HasMaxLength(450).IsRequired();
            entity.Property(r => r.AuthorName).HasMaxLength(200);
            entity.Property(r => r.Title).HasMaxLength(200);
            entity.Property(r => r.Rating).IsRequired();
            entity.Property(r => r.Body).HasMaxLength(4000);
            entity.Property(r => r.IsApproved).IsRequired();
            entity.Property(r => r.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");

            entity.HasOne(r => r.Product)
                .WithMany(p => p.Reviews)
                .HasForeignKey(r => r.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => r.ProductId).HasDatabaseName("IX_ProductReviews_ProductId");
            entity.HasIndex(r => r.UserId).HasDatabaseName("IX_ProductReviews_UserId");
        });

                modelBuilder.Entity<ProductMetadata>(entity =>
        {
            entity.ToTable("ProductMetadata");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.KeyName).HasMaxLength(100).IsRequired();
            entity.Property(m => m.KeyValue).HasMaxLength(2000).IsRequired();
            // C# `\x` is a hex escape — `"\x0000…"` becomes a literal NUL byte.
            // Use `\\x` so the backslash survives into the SQL literal Postgres parses.
            entity.Property(m => m.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");

            entity.HasOne(m => m.Product)
                .WithMany(p => p.Metadata)
                .HasForeignKey(m => m.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(m => m.ProductId).HasDatabaseName("IX_ProductMetadata_ProductId");
        });

        modelBuilder.Entity<ProductSpecification>(entity =>
        {
            entity.ToTable("ProductSpecifications");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).HasMaxLength(100).IsRequired();
            entity.Property(s => s.Value).HasMaxLength(1000).IsRequired();
            entity.Property(s => s.DisplayOrder).IsRequired();

            entity.HasOne(s => s.Product)
                .WithMany(p => p.Specifications)
                .HasForeignKey(s => s.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => s.ProductId).HasDatabaseName("IX_ProductSpecifications_ProductId");
        });

        modelBuilder.Entity<StockReservation>(entity =>
        {
            entity.ToTable("StockReservations");
            entity.HasKey(r => r.Id);
            // OrderId/SagaId are now nullable — Pending reservations have no
            // owning order yet (sync flow assigns them at Confirm). Saga path
            // populates both up-front via CreateConfirmed.
            entity.Property(r => r.OrderId);
            entity.Property(r => r.SagaId);
            entity.Property(r => r.UserId).HasMaxLength(450).IsRequired();
            entity.Property(r => r.Status).IsRequired();
            entity.Property(r => r.ExpiresAt).IsRequired();
            entity.Property(r => r.ConfirmedAt);
            entity.Property(r => r.ExpiredAt);
            entity.Property(r => r.ItemsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(r => r.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");

            // Optimistic concurrency via Postgres xmin system column
            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(r => r.OrderId).HasDatabaseName("IX_StockReservations_OrderId").IsUnique();
            entity.HasIndex(r => r.SagaId).HasDatabaseName("IX_StockReservations_SagaId");

            // Sweeper hot path: WHERE Status = Pending AND ExpiresAt <= now
            // ORDER BY ExpiresAt LIMIT N. Composite index keeps the scan
            // bounded as the table grows.
            entity.HasIndex(r => new { r.Status, r.ExpiresAt })
                .HasDatabaseName("IX_StockReservations_Status_ExpiresAt");
        });

        // MassTransit transactional outbox tables. Lives in the catalog DB so
        // SaveChangesAsync writes business state + outbox row in one txn —
        // the BusOutboxDeliveryService then publishes asynchronously to
        // RabbitMQ. Per-context (no shared OutboxMessage table across
        // services) is mandatory for ADR-0009 DB-per-service.
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
