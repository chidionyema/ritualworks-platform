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
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Hash).IsRequired().HasMaxLength(64);
            // Staff-level hardening: Unique index prevents race conditions in SHA-256 deduplication
            entity.HasIndex(e => e.Hash).IsUnique();
            entity.Property(e => e.MimeType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<string>();
        });
    }
}
