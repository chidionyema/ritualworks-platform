namespace Haworks.Contracts.Catalog;

/// <summary>
/// Published by catalog-svc whenever a product mutation invalidates its
/// cached representation — i.e. PUT or DELETE on <c>/api/products/{id}</c>
/// and any other handler that mutates an existing product. The publish is
/// part of the same EF outbox transaction as the DB write, so the broker
/// only sees the event after the row is durably committed.
///
/// Subscribed to by BffWeb's <c>ProductCacheInvalidatedBridge</c>,
/// which translates each event into a SignalR <c>OnCacheEvent</c> push so
/// the portfolio site's cache-invalidation demo can show invalidation as
/// it really happens. When <see cref="CorrelationId"/> is set the bridge
/// scopes the push to that demo session; when null (production calls
/// without a demo context) the bridge drops the event.
/// </summary>
public sealed record ProductCacheInvalidatedEvent : DomainEvent
{
    /// <summary>The product whose cached entry was invalidated.</summary>
    public required Guid ProductId { get; init; }

    /// <summary>
    /// Optional correlation back to the originating request — set by the
    /// demo proxy from the portfolio's session id, null for production calls.
    /// </summary>
    public Guid? CorrelationId { get; init; }

    /// <summary>The reason for invalidation: <c>updated</c>, <c>deleted</c>, <c>stock-reserved</c>, etc.</summary>
    public required string Reason { get; init; }

    /// <summary>Server-side observed version after the mutation, when meaningful (e.g. xmin row version on Update).</summary>
    public long? NewVersion { get; init; }
}
