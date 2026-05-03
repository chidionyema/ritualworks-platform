using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Payments.Infrastructure;

/// <summary>
/// DbContext for the Payments bounded context. Owns the Payments table +
/// MassTransit transactional outbox tables (InboxState, OutboxState,
/// OutboxMessage). Per ADR-0009 (DB-per-service), no entities reference
/// types from other contexts.
/// </summary>
public class PaymentDbContext : DbContext
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICurrentUserService _currentUserService;

    public PaymentDbContext(
        DbContextOptions<PaymentDbContext> options,
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

    public DbSet<Payment> Payments => Set<Payment>();

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

        modelBuilder.HasDefaultSchema("payments");

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments");
            entity.HasKey(p => p.Id);

            entity.Property(p => p.OrderId).IsRequired();
            entity.Property(p => p.UserId).HasMaxLength(450).IsRequired();
            entity.Property(p => p.SagaId).IsRequired();

            entity.Property(p => p.Amount).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(p => p.Tax).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(p => p.Currency).HasMaxLength(3).IsRequired();

            entity.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(p => p.Provider).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(p => p.PaymentMethod).HasMaxLength(50);

            entity.Property(p => p.ProviderSessionId).HasMaxLength(500);
            entity.Property(p => p.ProviderTransactionId).HasMaxLength(500);
            entity.Property(p => p.ProviderCheckoutUrl).HasMaxLength(2000);

            // RowVersion left as plain bytea (see Catalog rationale). Real
            // optimistic concurrency on payment state is via xmin shadow.
            entity.Property(p => p.RowVersion).HasDefaultValueSql("'\\x0000000000000000'::bytea");

            // xmin shadow concurrency token — Postgres native row-version.
            // Catches concurrent state transitions on the same Payment row
            // (e.g., webhook arriving while a refund is being processed).
            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(p => p.OrderId).HasDatabaseName("IX_Payments_OrderId");
            entity.HasIndex(p => p.UserId).HasDatabaseName("IX_Payments_UserId");
            entity.HasIndex(p => p.SagaId).HasDatabaseName("IX_Payments_SagaId");
            entity.HasIndex(p => new { p.Provider, p.ProviderSessionId })
                .HasDatabaseName("IX_Payments_Provider_ProviderSessionId");
        });

        // MassTransit transactional outbox tables.
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
