namespace Haworks.Search.Application.Models;

/// <summary>
/// Internal query shape passed to <c>ISearchIndex.SearchAsync</c>. Maps to
/// Meilisearch's search options — no public-API leakage either direction.
/// </summary>
public sealed record SearchQuery
{
    public string Query { get; init; } = "";

    /// <summary>Optional category filter — translates to a Meilisearch filter expression.</summary>
    public Guid? CategoryFilter { get; init; }

    /// <summary>Optional raw filter expression (escape hatch for indexer category-rename pagination).</summary>
    public string? Filter { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
