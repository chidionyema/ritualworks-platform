using Haworks.Search.Application.Models;

namespace Haworks.Search.Application.Interfaces;

/// <summary>
/// Thin abstraction over the underlying search engine (Meilisearch in v1).
/// Lets B5's consumers and B6's controller depend on the contract without
/// binding to the SDK — also makes the v2 swap to a different engine a
/// re-implementation, not a callsite rewrite.
/// </summary>
public interface ISearchIndex
{
    /// <summary>Upsert (Meilisearch addDocuments — primary-key dedupe). Awaits the index task.</summary>
    Task UpsertAsync(IReadOnlyCollection<ProductSearchDocument> docs, CancellationToken ct = default);

    /// <summary>Delete by primary key. Awaits the index task.</summary>
    Task DeleteAsync(string productIdKey, CancellationToken ct = default);

    /// <summary>Read a document by primary key. Returns null if absent — used by B5's OOO version guard.</summary>
    Task<ProductSearchDocument?> GetAsync(string productIdKey, CancellationToken ct = default);

    /// <summary>Run a query. The shape of the return enables Gemini-side re-ranking later.</summary>
    Task<SearchPage> SearchAsync(SearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Idempotent bootstrap — creates the index with the primary key, applies
    /// the canonical settings (searchableAttributes, filterableAttributes,
    /// rankingRules, etc.) per spec §4. Safe to call on every cold start.
    /// </summary>
    Task EnsureSettingsAsync(CancellationToken ct = default);
}
