using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.Notifications.Domain.Entities;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Persistence;
using Haworks.Notifications.Domain.ValueObjects;

namespace Haworks.Notifications.Infrastructure.Persistence;

public class NotificationsDbContext : DbContext
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICurrentUserService _currentUserService;

    public NotificationsDbContext(
        DbContextOptions<NotificationsDbContext> options,
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

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<Suppression> SuppressionList => Set<Suppression>();
    public DbSet<RateLimitBucket> RateLimitBuckets => Set<RateLimitBucket>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseLoggerFactory(_loggerFactory);
        if (_environment.IsDevelopment()) optionsBuilder.EnableSensitiveDataLogging();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("notifications");

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.ToTable("Notifications");
            entity.HasKey(n => n.Id);
            entity.Property(n => n.UserId).HasMaxLength(450);
            entity.Property(n => n.Recipient).HasMaxLength(254).IsRequired();
            entity.Property(n => n.Channel).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(n => n.TemplateId).HasMaxLength(100).IsRequired();
            entity.Property(n => n.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(n => n.Priority).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(n => n.Subject).HasMaxLength(500);
            entity.Property(n => n.Body).IsRequired();
            entity.Property(n => n.IdempotencyKey).HasMaxLength(200);

            entity.OwnsMany(n => n.DeliveryAttempts, a =>
            {
                a.ToTable("DeliveryAttempts");
                a.WithOwner().HasForeignKey("NotificationId");
                a.Property<Guid>("Id");
                a.HasKey("Id");
                a.Property(x => x.ProviderName).HasMaxLength(50).IsRequired();
                a.Property(x => x.ProviderMessageId).HasMaxLength(200);
                a.Property(x => x.ErrorMessage).HasMaxLength(2000);
            });

            entity.HasIndex(n => n.UserId);
            entity.HasIndex(n => n.Recipient);
            entity.HasIndex(n => n.Status);
            entity.HasIndex(n => n.IdempotencyKey).IsUnique();
        });

        modelBuilder.Entity<NotificationTemplate>(entity =>
        {
            entity.ToTable("NotificationTemplates");
            entity.HasKey(t => new { t.TemplateId, t.Channel, t.Locale, t.Version });
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.Category).HasMaxLength(100).IsRequired();
            entity.Property(t => t.SubjectTemplate).HasMaxLength(500).IsRequired();
            entity.Property(t => t.BodyTemplate).IsRequired();
        });

        modelBuilder.Entity<NotificationPreference>(entity =>
        {
            entity.ToTable("NotificationPreferences");
            entity.HasKey(p => new { p.UserId, p.Category, p.Channel });
        });

        modelBuilder.Entity<Suppression>(entity =>
        {
            entity.ToTable("SuppressionList");
            entity.HasKey(s => new { s.RecipientHash, s.Channel });
            entity.Property(s => s.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<RateLimitBucket>(entity =>
        {
            entity.ToTable("RateLimitBuckets");
            entity.HasKey(b => new { b.BucketKey, b.WindowStart });
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
