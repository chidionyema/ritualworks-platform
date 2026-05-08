namespace Haworks.Search.Application.Models;

/// <summary>
/// Document shape stored in the Meilisearch <c>products</c> index. Mirrors
/// spec §4 verbatim — the Meilisearch primary key is the dash-free uuid
/// (<see cref="ProductIdKey"/>); the original uuid is round-tripped via
/// <see cref="ProductId"/>.
/// </summary>
public sealed record ProductSearchDocument
{
    /// <summary>Dash-free uuid (Guid.ToString("N")) — Meilisearch primary key.</summary>
    public required string ProductIdKey { get; init; }

    /// <summary>Original uuid string, returned to clients.</summary>
    public required string ProductId { get; init; }

    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string CategoryId { get; init; }
    public required string CategoryName { get; init; }
    public required decimal UnitPrice { get; init; }
    public required bool IsInStock { get; init; }
    public required bool IsListed { get; init; }

    /// <summary>Source row version for OOO event suppression (B5 enforces).</summary>
    public required long SourceVersion { get; init; }

    /// <summary>Unix epoch seconds — used as freshness tiebreaker in ranking rules.</summary>
    public required long IndexedAt { get; init; }
}
