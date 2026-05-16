using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Haworks.Content.Domain.Entities;
using Haworks.BuildingBlocks.CurrentUser;
using Haworks.BuildingBlocks.Persistence;

namespace Haworks.Content.Infrastructure.Persistence;

/// <summary>
/// DbContext for Content bounded context.
/// Manages ContentEntity, ContentMetadata, and ContentVersions.
/// </summary>
public class ContentDbContext : DbContext
{
    private readonly IHostEnvironment _environment;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICurrentUserService _currentUserService;

    public ContentDbContext(
        DbContextOptions<ContentDbContext> options,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory,
        ICurrentUserService currentUserService)
        : base(options)
    {
        _environment = environment;
        _loggerFactory = loggerFactory;
        _currentUserService = currentUserService;
    }

    public DbSet<ContentEntity> Contents => Set<ContentEntity>();
    public DbSet<ContentMetadata> ContentMetadata => Set<ContentMetadata>();
    public DbSet<ContentVersion> ContentVersions => Set<ContentVersion>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        ChangeTracker.LazyLoadingEnabled = false;

        optionsBuilder.UseLoggerFactory(_loggerFactory);

        // Enable sensitive data logging only in development
        if (_environment.IsDevelopment())
        {
            optionsBuilder.EnableSensitiveDataLogging();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Set default schema for Content context
        modelBuilder.HasDefaultSchema("content");

        // ContentEntity configuration
        modelBuilder.Entity<ContentEntity>(entity =>
        {
            entity.ToTable("Contents");

            // Use PostgreSQL xmin as optimistic concurrency token
            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            entity.Property(c => c.EntityType)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(c => c.BlobName)
                .HasMaxLength(500);

            entity.Property(c => c.FileName)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(c => c.BucketName)
                .HasMaxLength(100);

            entity.Property(c => c.ObjectName)
                .HasMaxLength(500);

            entity.Property(c => c.ETag)
                .HasMaxLength(200);

            entity.Property(c => c.Slug)
                .HasMaxLength(500);

            entity.Property(c => c.StorageDetails)
                .HasMaxLength(1000);

            entity.Property(c => c.Path)
                .HasMaxLength(1000);

            entity.Property(c => c.Url)
                .HasMaxLength(2000);

            // New lifecycle / S3-multipart fields.
            entity.Property(c => c.Status)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(c => c.UploadKind)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(c => c.OwnerUserId)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(c => c.ContentTypeMime)
                .HasMaxLength(127)
                .IsRequired();

            entity.Property(c => c.S3UploadId)
                .HasMaxLength(256);

            entity.Property(c => c.Sha256Checksum)
                .HasMaxLength(64);

            entity.Property(c => c.QuarantineReason)
                .HasMaxLength(500);

            entity.Property(c => c.FailureReason)
                .HasMaxLength(500);

            entity.HasIndex(c => new { c.EntityId, c.EntityType })
                .HasDatabaseName("IX_Contents_EntityRef");

            entity.HasIndex(c => c.Slug)
                .HasDatabaseName("IX_Contents_Slug");

            entity.HasIndex(c => c.ContentType)
                .HasDatabaseName("IX_Contents_ContentType");

            // Sweeper queries Status + CreatedAt to find Pending rows
            // past TTL. Index keeps that scan cheap.
            entity.HasIndex(c => new { c.Status, c.CreatedAt })
                .HasDatabaseName("IX_Contents_Status_CreatedAt");

            entity.HasIndex(c => c.OwnerUserId)
                .HasDatabaseName("IX_Contents_OwnerUserId");

            // Metadata relationship
            entity.HasMany(c => c.Metadata)
                .WithOne(m => m.Content)
                .HasForeignKey(m => m.ContentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Versions relationship
            entity.HasMany(c => c.Versions)
                .WithOne(v => v.Content)
                .HasForeignKey(v => v.ContentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ContentMetadata configuration
        modelBuilder.Entity<ContentMetadata>(entity =>
        {
            entity.ToTable("ContentMetadata");

            entity.HasKey(m => m.Id);

            entity.Property(m => m.Key)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(m => m.Value)
                .HasMaxLength(2000);

            entity.HasIndex(m => new { m.ContentId, m.Key })
                .IsUnique()
                .HasDatabaseName("IX_ContentMetadata_ContentKey");
        });

        // ContentVersion configuration
        modelBuilder.Entity<ContentVersion>(entity =>
        {
            entity.ToTable("ContentVersions");

            entity.Property(v => v.VersionInfo)
                .HasMaxLength(500);

            entity.HasIndex(v => v.ContentId)
                .HasDatabaseName("IX_ContentVersions_ContentId");
        });

        // MassTransit EF Core outbox entities (transactional outbox pattern).
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AddAuditInfo();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void AddAuditInfo()
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
