using MassTransit;

namespace Haworks.Media.Api.Infrastructure;

public class MediaDbContext : DbContext
{
    public MediaDbContext(DbContextOptions<MediaDbContext> options) : base(options) { }

    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("media");

        modelBuilder.Entity<MediaFile>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasQueryFilter(f => !f.IsDeleted);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Hash).IsRequired().HasMaxLength(64);
            // Unique index scoped per owner — different users may upload the same file
            entity.HasIndex(e => new { e.Hash, e.OwnerId }).IsUnique();
            entity.Property(e => e.MimeType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<string>();
            entity.Property(e => e.UploadKind).HasConversion<string>().HasDefaultValue(UploadKind.SinglePart);
            entity.Property(e => e.S3UploadId).HasMaxLength(256);
            entity.Property(e => e.PartCount).HasDefaultValue(0);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.OwnerId);

            entity.Property(e => e.IsDeleted).HasDefaultValue(false);
            entity.Property(e => e.DeletedAt);
            entity.Property(e => e.UpdatedAt);
            entity.Property(e => e.UpdatedBy).HasMaxLength(128);

            entity.Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        });

        // MassTransit transactional outbox tables — required for Publish() to write
        // to OutboxMessage in the same EF transaction as domain state changes.
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}
