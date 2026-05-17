using Haworks.CheckoutOrchestrator.Application.Interfaces;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Haworks.CheckoutOrchestrator.Infrastructure;

/// <summary>
/// DbContext for the CheckoutOrchestrator service. Owns the
/// CheckoutSagaState table + the MassTransit per-context outbox/inbox
/// tables. Per ADR-0009 the saga has no business state of its own beyond
/// the snapshot it needs to drive orchestration.
/// </summary>
public class CheckoutDbContext : DbContext, ICheckoutDbContext
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;

    public CheckoutDbContext(
        DbContextOptions<CheckoutDbContext> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory)
        : base(options)
    {
        _environment = environment;
        _loggerFactory = loggerFactory;
        ChangeTracker.LazyLoadingEnabled = false;
    }

    public DbSet<CheckoutSagaState> CheckoutSagas => Set<CheckoutSagaState>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseLoggerFactory(_loggerFactory);
        if (_environment.IsDevelopment()) optionsBuilder.EnableSensitiveDataLogging();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("checkout");

        modelBuilder.Entity<CheckoutSagaState>(entity =>
        {
            entity.ToTable("CheckoutSagas");
            entity.HasKey(s => s.CorrelationId);

            entity.Property(s => s.CurrentState).HasMaxLength(64).IsRequired();
            entity.Property(s => s.UserId).HasMaxLength(450).IsRequired();
            entity.Property(s => s.CustomerEmail).HasMaxLength(254).IsRequired();
            entity.Property(s => s.TotalAmount).HasColumnType("numeric(18,2)");
            entity.Property(s => s.Currency).HasMaxLength(3).IsRequired();
            entity.Property(s => s.IdempotencyKey).HasMaxLength(200);
            entity.Property(s => s.LineItemsJson).HasColumnType("jsonb");
            entity.Property(s => s.ReservedItemsJson).HasColumnType("jsonb");
            entity.Property(s => s.PaymentSessionId).HasMaxLength(500);
            entity.Property(s => s.PaymentCheckoutUrl).HasMaxLength(2000);
            entity.Property(s => s.FailureReason).HasMaxLength(1000);

            // MT optimistic concurrency on saga state. Both the Version
            // column AND the xmin shadow are wired — Version covers MT's
            // own write-time check, xmin covers raw EF saves outside the
            // MT pipeline (e.g., REST status queries that read+write).
            entity.Property(s => s.Version).IsConcurrencyToken();
            entity.Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.HasIndex(s => s.OrderId).IsUnique().HasDatabaseName("IX_CheckoutSagas_OrderId");
            entity.HasIndex(s => s.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL").HasDatabaseName("IX_CheckoutSagas_IdempotencyKey");
            entity.HasIndex(s => s.CurrentState).HasDatabaseName("IX_CheckoutSagas_CurrentState");
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}
