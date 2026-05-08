namespace Haworks.Search.Application.Models;

/// <summary>
/// Result envelope for <c>ISearchIndex.SearchAsync</c>. The HTTP-facing
/// response shape (<see cref="ProductSearchDocument"/> + score + snippet)
/// is built from this in B6's controller.
/// </summary>
public sealed record SearchPage
{
    public required IReadOnlyList<ProductSearchDocument> Hits { get; init; }
    public required int TotalHits { get; init; }
    public required long TookMs { get; init; }
}
