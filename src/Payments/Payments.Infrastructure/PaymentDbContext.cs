using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.Payments.Domain;
using Haworks.Payments.Application.Interfaces;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Payments.Infrastructure;

public class PaymentDbContext : DbContext, IPaymentDbContext
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
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<RefundSagaState> RefundSagas => Set<RefundSagaState>();
    public DbSet<SubscriptionSagaState> SubscriptionSagas => Set<SubscriptionSagaState>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseLoggerFactory(_loggerFactory);
        if (_environment.IsDevelopment()) optionsBuilder.EnableSensitiveDataLogging();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("payments");

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments");
            entity.HasKey(p => p.Id);

            entity.Property(p => p.Amount).HasColumnType("numeric(18,2)");
            entity.Property(p => p.Currency).HasMaxLength(3);
            entity.Property(p => p.Status).HasConversion<string>();
            entity.Property(p => p.Provider).HasConversion<string>();
            entity.Property(p => p.UserId).HasMaxLength(100);
            entity.Property(p => p.ProviderTransactionId).HasMaxLength(255);
            entity.Property(p => p.ProviderSessionId).HasMaxLength(255);

            entity.HasIndex(p => p.OrderId).IsUnique();
            entity.HasIndex(p => p.ProviderTransactionId);
            entity.HasIndex(p => p.ProviderSessionId);
        });

        modelBuilder.Entity<Subscription>(entity =>
        {
            entity.ToTable("Subscriptions");
            entity.HasKey(s => s.Id);

            entity.Property(s => s.Status).HasConversion<string>();
            entity.Property(s => s.Provider).HasConversion<string>();
            entity.Property(s => s.UserId).HasMaxLength(100);
            entity.Property(s => s.ProviderSubscriptionId).HasMaxLength(255);
            entity.Property(s => s.PlanId).HasMaxLength(255);

            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.ProviderSubscriptionId);
        });

        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.ToTable("SubscriptionPlans");
            entity.HasKey(p => p.Id);

            entity.Property(p => p.Name).HasMaxLength(100);
            entity.Property(p => p.InternalPlanId).HasMaxLength(255);
            entity.Property(p => p.Price).HasColumnType("numeric(18,2)");
        });

        modelBuilder.Entity<WebhookEvent>(entity =>
        {
            entity.ToTable("WebhookEvents");
            entity.HasKey(w => w.Id);

            entity.Property(w => w.Provider).HasConversion<string>();
            entity.Property(w => w.ProviderEventId).HasMaxLength(255);
            entity.Property(w => w.EventType).HasMaxLength(100);
            entity.Property(w => w.EventJson).HasColumnType("text");
            entity.Property(w => w.Error).HasMaxLength(2000);

            entity.HasIndex(w => new { w.Provider, w.ProviderEventId }).IsUnique().HasDatabaseName("IX_WebhookEvents_Provider_Id");
        });

        modelBuilder.Entity<RefundSagaState>(entity =>
        {
            entity.ToTable("RefundSagas");
            entity.HasKey(s => s.CorrelationId);

            entity.Property(s => s.OrderId).IsRequired();
            entity.Property(s => s.PaymentId).IsRequired();
            entity.Property(s => s.RefundId).IsRequired();
            entity.Property(s => s.Amount).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(s => s.Currency).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Provider).HasMaxLength(20);
            entity.Property(s => s.ProviderRefundId).HasMaxLength(500);
            entity.Property(s => s.CurrentState).HasMaxLength(100);
            entity.Property(s => s.FailureCategory).HasConversion<string>().HasMaxLength(50);

            entity.HasIndex(s => s.OrderId);
            entity.HasIndex(s => s.PaymentId);
            entity.HasIndex(s => s.ProviderRefundId);

            // Concurrency protection (XC-01/RS-01)
            entity.Property(s => s.Version).IsConcurrencyToken();
            entity.Property<uint>("xmin")
                .HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<SubscriptionSagaState>(entity =>
        {
            entity.ToTable("SubscriptionSagas");
            entity.HasKey(s => s.CorrelationId);

            entity.Property(s => s.ProviderSubscriptionId).HasMaxLength(255).IsRequired();
            entity.Property(s => s.UserId).HasMaxLength(100).IsRequired();
            entity.Property(s => s.PlanId).HasMaxLength(100).IsRequired();
            entity.Property(s => s.Currency).HasMaxLength(3).IsRequired();
            entity.Property(s => s.Amount).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(s => s.CurrentState).HasMaxLength(100);

            entity.HasIndex(s => s.ProviderSubscriptionId);
            entity.HasIndex(s => s.UserId);

            // Concurrency protection (XC-01/RS-02/SS-05)
            entity.Property(s => s.Version).IsConcurrencyToken();
            entity.Property<uint>("xmin")
                .HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        OnBeforeSaving();
        return await base.SaveChangesAsync(cancellationToken);
    }

    private void OnBeforeSaving()
    {
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = DateTime.UtcNow;
                entry.Entity.CreatedBy = _currentUserService.UserId ?? "system";
                entry.Entity.CreatedFromIp = _currentUserService.ClientIp ?? "unknown";
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.LastModifiedDate = DateTime.UtcNow;
                entry.Entity.LastModifiedBy = _currentUserService.UserId ?? "system";
                entry.Entity.ModifiedFromIp = _currentUserService.ClientIp ?? "unknown";
            }
        }
    }
}
