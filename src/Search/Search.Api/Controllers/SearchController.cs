using System.ComponentModel.DataAnnotations;
using Haworks.Search.Application.Indexing;
using Haworks.Search.Application.Interfaces;
using Haworks.Search.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Haworks.Search.Api.Controllers;

[ApiController]
[Route("search")]
[Authorize]
public sealed class SearchController : ControllerBase
{
    private readonly ISearchIndex _index;
    private readonly ILogger<SearchController> _logger;

    public SearchController(ISearchIndex index, ILogger<SearchController> logger)
    {
        _index = index;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<SearchResponse>> Search(
        [FromQuery, Required, MinLength(1), MaxLength(200)] string q,
        [FromQuery] Guid? categoryId,
        [FromQuery, Range(1, 10_000)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var sanitized = SearchQuerySanitizer.Sanitize(q);
        if (sanitized.Length == 0)
        {
            return BadRequest(new { error = "q must contain at least one searchable term" });
        }

        var page_ = await _index.SearchAsync(new SearchQuery
        {
            Query = sanitized,
            CategoryFilter = categoryId,
            Page = page,
            PageSize = pageSize,
        }, ct);

        var hits = page_.Hits.Select(h => new SearchHitResponse
        {
            ProductId    = h.ProductId,
            Name         = h.Name,
            // Placeholder snippet — Meilisearch's attributesToHighlight will
            // populate this in v2. Spec §3.1 keeps the field stable.
            Snippet      = h.Name,
            CategoryId   = h.CategoryId,
            CategoryName = h.CategoryName,
            UnitPrice    = h.UnitPrice,
            IsInStock    = h.IsInStock,
            // Placeholder score — Meilisearch's showRankingScore will
            // populate this in v2. Same spec stability rationale.
            Score        = 1.0,
        }).ToArray();

        return Ok(new SearchResponse
        {
            Query     = q,
            CategoryId = categoryId,
            Page      = page,
            PageSize  = pageSize,
            TotalHits = page_.TotalHits,
            TookMs    = page_.TookMs,
            Hits      = hits,
        });
    }

    [HttpPost("saved")]
    public async Task<IActionResult> SaveSearch(
        [FromBody] SearchQuery query,
        CancellationToken ct = default)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value 
            ?? User.FindFirst("sub")?.Value 
            ?? "anonymous";

        var id = Guid.NewGuid().ToString("N");
        await _index.RegisterSavedSearchAsync(id, userId, query, ct);
        
        return Created($"/search/saved/{id}", new { id });
    }
}
