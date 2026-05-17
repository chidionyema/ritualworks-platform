using Microsoft.EntityFrameworkCore;

namespace Haworks.BuildingBlocks.Idempotency;

/// <summary>
/// Interface for any DbContext that hosts the idempotency journal table.
/// Each service's DbContext implements this to get automatic idempotency
/// without a separate database.
/// </summary>
public interface IIdempotencyJournalDbContext
{
    DbSet<IdempotencyJournalEntry> IdempotencyJournal { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
