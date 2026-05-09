using Haworks.Notifications.Domain.Entities;

namespace Haworks.Notifications.Application.Commands;

/// <summary>
/// Application-owned write port for the Notification aggregate.
///
/// Lives in <c>Application/Commands</c> rather than <c>Domain/Interfaces</c>
/// because L1.G owns the surface and Notifications.Application can't take a
/// dependency on Notifications.Infrastructure (Clean Arch direction). A future
/// L1.A/L1.F follow-up may relocate this to <c>Notifications.Domain</c> once
/// the persistence shape stabilises.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Returns the existing notification id for the supplied idempotency key,
    /// or <c>null</c> if none exists. Implementations MUST execute as
    /// <c>AsNoTracking</c> so callers can safely Add a new aggregate after a
    /// negative lookup without triggering a duplicate-tracking exception.
    /// </summary>
    Task<Guid?> FindIdByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Stages a new notification for insert. Persistence is deferred to
    /// <see cref="SaveChangesAsync"/> so the caller can publish a domain
    /// event in the same EF transaction (outbox guarantee).
    /// </summary>
    void Add(Notification notification);

    /// <summary>
    /// Returns the tracked notification matching the supplied id (with
    /// delivery attempts populated), or <c>null</c> if no row exists.
    /// </summary>
    Task<Notification?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
