using Haworks.Webhooks.Domain;
using Haworks.Webhooks.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Webhooks.Infrastructure.Persistence;

public sealed class WebhooksDbContext(DbContextOptions<WebhooksDbContext> options) : DbContext(options), IWebhooksDbContext
{
    public DbSet<WebhookSubscription> Subscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();
    public DbSet<WebhookDeliveryAttempt> DeliveryAttempts => Set<WebhookDeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("webhooks");

        modelBuilder.Entity<WebhookSubscription>(entity =>
        {
            entity.ToTable("webhook_subscriptions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PartnerId).IsRequired();
            entity.Property(e => e.Url).IsRequired();
            entity.Property(e => e.Secret).IsRequired();
            entity.Property(e => e.SecretHash).IsRequired();
            entity.Property(e => e.SecretPreview).IsRequired();
            entity.Property(e => e.Events).IsRequired();
            
            entity.HasIndex(e => e.PartnerId).HasFilter("\"DeletedAt\" IS NULL");
            entity.HasIndex(e => e.Events).HasMethod("gin");
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.ToTable("webhook_deliveries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventId).IsRequired();
            entity.Property(e => e.EventType).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().IsRequired();
            
            entity.HasOne<WebhookSubscription>()
                .WithMany()
                .HasForeignKey(e => e.SubscriptionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.Status, e.NextAttemptAt }).HasFilter("\"Status\" IN ('Pending', 'Failed')");
            entity.HasIndex(e => e.EventId);
            entity.HasIndex(e => new { e.SubscriptionId, e.CreatedAt });
        });

        modelBuilder.Entity<WebhookDeliveryAttempt>(entity =>
        {
            entity.ToTable("webhook_delivery_attempts");
            entity.HasKey(e => e.Id);
            
            entity.HasOne<WebhookDelivery>()
                .WithMany(d => d.DeliveryAttempts)
                .HasForeignKey(e => e.DeliveryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.DeliveryId, e.AttemptIndex });
        });
    }
}
