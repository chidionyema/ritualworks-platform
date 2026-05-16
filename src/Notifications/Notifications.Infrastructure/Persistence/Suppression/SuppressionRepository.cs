using Haworks.Notifications.Application.Suppression;
using Haworks.Notifications.Domain.Entities;
using Haworks.Notifications.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Haworks.Notifications.Infrastructure.Persistence.SuppressionStore;

/// <summary>
/// EF Core suppression repository. <see cref="AddAsync"/> is idempotent — a
/// duplicate insert (same RecipientHash + Channel) is treated as a no-op so
/// retried bounce / complaint events don't blow up the saga.
/// </summary>
public sealed class SuppressionRepository : ISuppressionRepository
{
    private readonly NotificationsDbContext _dbContext;

    public SuppressionRepository(NotificationsDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string recipientHash, NotificationChannel channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientHash);

        return _dbContext.SuppressionList
            .AsNoTracking()
            .AnyAsync(s => s.RecipientHash == recipientHash && s.Channel == channel);
    }

    /// <inheritdoc />
    public async Task AddAsync(Haworks.Notifications.Domain.Entities.Suppression suppression)
    {
        ArgumentNullException.ThrowIfNull(suppression);

        // Idempotent insert: if (RecipientHash, Channel) already exists,
        // do nothing. The composite PK is enforced by the DB; this guard
        // avoids the round-trip to the constraint violation handler in
        // the common (already-suppressed) path.
        var alreadyExists = await _dbContext.SuppressionList
            .AsNoTracking()
            .AnyAsync(s => s.RecipientHash == suppression.RecipientHash
                        && s.Channel == suppression.Channel)
            .ConfigureAwait(false);

        if (alreadyExists)
        {
            return;
        }

        _dbContext.SuppressionList.Add(suppression);

        try
        {
            await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another writer — re-check; if the row is now
            // there, the operation is still effectively idempotent. Otherwise
            // the failure was real and we surface it.
            var existsNow = await ExistsByPkAsync(suppression).ConfigureAwait(false);
            if (!existsNow)
            {
                throw;
            }

            // Detach our duplicate so the change tracker doesn't keep retrying.
            _dbContext.Entry(suppression).State = EntityState.Detached;
        }
    }

    private Task<bool> ExistsByPkAsync(Haworks.Notifications.Domain.Entities.Suppression suppression) =>
        _dbContext.SuppressionList
            .AsNoTracking()
            .AnyAsync(s => s.RecipientHash == suppression.RecipientHash
                        && s.Channel == suppression.Channel);
}
