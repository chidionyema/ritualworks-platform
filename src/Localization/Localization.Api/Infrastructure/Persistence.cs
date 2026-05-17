using System.Text.Json;
using Haworks.Localization.Api.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Haworks.Localization.Api.Infrastructure;

public class LocalizationDbContext : DbContext
{
    public LocalizationDbContext(DbContextOptions<LocalizationDbContext> options) : base(options) { }

    public DbSet<Translation> Translations => Set<Translation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("localization");
        modelBuilder.ApplyConfiguration(new TranslationConfiguration());

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}

public class TranslationConfiguration : IEntityTypeConfiguration<Translation>
{
    public void Configure(EntityTypeBuilder<Translation> builder)
    {
        builder.HasKey(t => t.Id);
        builder.HasIndex(t => t.Key).IsUnique();

        builder.Property(t => t.Values)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new())
            .HasColumnType("jsonb");

        builder.Property(t => t.UpdatedBy).HasMaxLength(256);
        builder.Property(t => t.UpdatedAt);

        // Optimistic concurrency via PostgreSQL xmin system column
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
