namespace Haworks.Contracts.Catalog;

/// <summary>
/// Published by catalog-svc whenever a category is renamed via
/// <c>UpdateCategoryCommand</c>. Search-svc consumes it to re-denormalize
/// the cached <c>categoryName</c> on every product in the affected
/// category — without it, renames silently rot the search index.
///
/// Published in the same EF outbox transaction as the DB write, so the
/// broker only sees the event after the row is durably committed.
/// </summary>
public sealed record CategoryUpdatedEvent : DomainEvent
{
    /// <summary>The category whose attributes changed.</summary>
    public required Guid CategoryId { get; init; }

    /// <summary>The new (post-rename) name.</summary>
    public required string Name { get; init; }
}
