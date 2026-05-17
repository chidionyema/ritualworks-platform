using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// EF Core configuration for the IdempotencyJournal table.
/// Call <c>modelBuilder.ApplyConfiguration(new IdempotencyJournalEntityConfiguration())</c>
/// in each service's DbContext OnModelCreating.
/// </summary>
public sealed class IdempotencyJournalEntityConfiguration : IEntityTypeConfiguration<IdempotencyJournalEntry>
{
    public void Configure(EntityTypeBuilder<IdempotencyJournalEntry> builder)
    {
        builder.ToTable("idempotency_journal");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.IdempotencyKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.CommandType)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.ResponseJson)
            .HasColumnType("jsonb");

        builder.Property(e => e.CreatedAt)
            .IsRequired();

        builder.Property(e => e.ExpiresAt)
            .IsRequired();

        // CRITICAL: This unique index is the idempotency enforcement mechanism.
        // Concurrent inserts with the same key will fail with 23505.
        builder.HasIndex(e => e.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ix_idempotency_journal_key");

        // Index for TTL cleanup job
        builder.HasIndex(e => e.ExpiresAt)
            .HasDatabaseName("ix_idempotency_journal_expires");
    }
}
